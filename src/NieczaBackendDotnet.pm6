our $PassSimplifier;

class NieczaBackendDotnet;

has $.safemode = False;
has @.obj_dirs;
has $.run_args = [];

enum Phaser < INIT END UNIT_INIT KEEP UNDO LEAVE ENTER PRE POST CATCH CONTROL CHECK >;
enum MultiMode ( only => 0, proto => 4, multi => 8 );
enum SubVisibility ( normal => 0, private => 1, sub => 2 );

method phaser($n) { +Phaser.($n) }
method sub_visibility($n) { +SubVisibility.($n) }
method multi_mode($n) { +MultiMode.($n) }

sub locstr($fo, $lo, $fn, $ln) {
    $fo := $fo // '???';
    $lo := $lo // '???';
    $fn := $fn // '???';
    $ln := $ln // '???';

    $fn eq $fo ?? " (see line $lo)" !! " (see $fo line $lo)";
}

sub upcalled(*@args) {
    my $v = $*compiler.verbose;
    given @args[0] {
        when "EVAL" {
            my $*IN_EVAL = True;
            say "EVAL: @args[1] from @args[2].name()" if $v;
            return $*compiler.compile_string(@args[1], True, :evalmode,
                :outer(@args[2]), :outer_frame(@args[3]));
        }
        when "check_dated" {
            shift @args;
            for @args -> $module, $hash {
                my ($file, $src) = #OK
                    $*compiler.module_finder.load_module($module);
                my $trueh = gethash($src);
                say "check-dated $module: was $hash now $trueh" if $v;
                # allow for source concealment
                return "no" unless $hash eq $trueh || $hash eq gethash($trueh);
            }
            return "ok";
        }
        when "compile_unit" {
            say "autocompiling @args[1]..." if $v;
            say "[auto-compiling setting]" if @args[1] eq 'CORE' && !$v;
            $*compiler.compile_module(@args[1]);
            say "[done]" if @args[1] eq 'CORE' && !$v;
            say "done compiling @args[1]." if $v;
            return;
        }
        say "upcall: @args.join('|')";
        die "ERROR";
    }
}

class Param { ... }
class Unit { ... }
class StaticSub { ... }
class Type { ... }
class Value { ... }

method new(*%_) {
    my $self = callsame;
    Q:CgOp { (rnull (cb_init_slave {&upcalled} (@ {$self.obj_dirs}) {Unit} {StaticSub} {Type} {Param} {Value})) };
    downcall("safemode") if $self.safemode;
    $self;
}

method downcall(|args) { downcall(|args) }
sub downcall(*@args) {
    Q:CgOp { (cb_downcall {@args}) }
}
method make_role($name, $meth) {
    Q:CgOp { (cat_mixin_role (obj_getstr {$name}) (@ {$meth})) }
}
method prune_match($match) {
    Q:CgOp { (cb_prune_match {$match}) }
}

method cached_but($cls, $role) {
    # TODO: Object hashes!
    Q:CgOp { (cb_cached_but (@ {&infix:<but>}) {$cls} {$role}) };
}

sub gethash($str) { Q:CgOp { (box Str (cb_do_hash (obj_getstr {$str}))) } }
method gethash($str) { gethash($str) }
sub execname() { Q:CgOp { (box Str (cb_exec_name)) } }

method accept($unit, :$filename, :$run, :$evalmode, :$repl) {
    if $run {
        downcall("setnames", execname(), $filename) unless $repl;
        downcall("run_unit", $unit, ?$evalmode, @$!run_args);
        if $repl {
            $*repl_outer_frame = $unit.replrun;
            $*repl_outer = $unit.mainline;
        }
        return;
    }
    downcall("save_unit", $unit);
    $*repl_outer = $unit.mainline if $repl;
}

class Param {
    method kind { "param" }
    method FALLBACK($name, *@args) { downcall("param_$name", self, @args) }
}

class Value {
    method kind { "value" }
    method FALLBACK($name, *@args) { downcall("value_$name", self, @args) }
}

class StaticSub {
    method kind { "sub" }
    method FALLBACK($name, *@args) { downcall("sub_$name", self, @args) }

    method lex_names() { downcall("lex_names", self) }
    method lookup_lex($name, $file?, $line?) {
        downcall("sub_lookup_lex", self, $name, $file, $line//0);
    }

    method unused_lexicals() { downcall("unused_lexicals", self) }
    method unit() { downcall("sub_get_unit", self) }
    method is($o) { downcall("equal_handles", self, $o) }

    method !signature($sig) {
        my @args;
        for @( $sig.params ) {
            push @args, downcall("param_new", self, .flags, .name, .slot,
                @( .names ), Str, .mdefault, .tclass, .attribute,
                .attribute_type);
            @args[*-1].subsig(self!signature(.subsig)) if .subsig;
            @args[*-1].constraints(@(.where)) if .where;
        }
        @args;
    }

    method set_signature($sig) {
        if !$sig {
            downcall("sub_no_signature", self);
            return;
        }
        downcall("set_signature", self, self!signature($sig));
    }

    method add_exports($name, $obj, $tags) {
        my $u = self.unit;
        for @$tags -> $tag {
            $u.bind($u.rel_pkg(self.cur_pkg, 'EXPORT', $tag, :auto).who,
                $name, $obj);
        }
        +$tags;
    }

    # TODO: prevent foo; sub foo { } from warning undefined
    # needs a %*MYSTERY check when evaluating unused variables
    method _addlex_result(*@args) {
        given @args[0] {
            when 'collision' {
                my ($ , $slot, $nf,$nl,$of,$ol) = @args;
                my $l = locstr($of, $ol, $nf, $nl);
                if $slot ~~ /^\w/ {
                    die "Illegal redeclaration of symbol '$slot'$l";
                } elsif $slot ~~ /^\&/ {
                    die "Illegal redeclaration of routine '$slot.substr(1)'$l";
                } else {
                    $*worry.("Useless redeclaration of variable $slot$l");
                }
            }
            when 'already-bound' {
                my ($ , $slot, $count, $line, $nf,$nl,$of,$ol) = @args;
                my $truename = $slot;
                $truename ~~ s/<?before \w>/OUTER::/ for ^$count;
                die "Lexical symbol '$slot' is already bound to an outer symbol{locstr($of, $ol, $nf, $nl)};\n  the implicit outer binding at line $line must be rewritten as $truename\n  before you can unambiguously declare a new '$slot' in this scope";
            }
            when 'sub' {
                my ($ , $slot) = @args;
                if %*MYSTERY{substr($slot,1)} -> $my {
                    my $ix = $my<line>.index(',');
                    self.lookup_lex($slot, $*FILE<name>,
                        +(defined($ix) ?? substr($my<line>,0,$ix) !! $my<line>));
                }
            }
        }
    }

    method add_my_name($name, :$file, :$line, :$pos, :$noinit, :$defouter,
            :$roinit, :$list, :$hash, :$typeconstraint) {
        self._addlex_result(downcall("add_my_name", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $typeconstraint,
            ($noinit ?? 1 !! 0) + ($roinit ?? 2 !! 0) + ($defouter ?? 4 !! 0) +
            ($list ?? 8 !! 0) + ($hash ?? 16 !! 0)));
    }
    method add_hint($name, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_hint", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1)));
    }
    method add_label($name, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_label", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1)));
    }
    method add_dispatcher($name, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_dispatcher", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1)));
    }
    method add_common_name($name, $pkg, $pname, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_common_name", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $pkg, ~$pname));
    }
    method add_state_name($name, $backing, :$file, :$line, :$pos, :$noinit,
            :$defouter, :$roinit, :$list, :$hash, :$typeconstraint) {
        self._addlex_result(downcall("add_state_name", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $typeconstraint,
            ($noinit ?? 1 !! 0) + ($roinit ?? 2 !! 0) + ($defouter ?? 4 !! 0) +
            ($list ?? 8 !! 0) + ($hash ?? 16 !! 0),
            $backing));
    }
    method add_attr_alias($name, $type, $aname, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_attr_alias", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $type, $aname));
    }
    method add_alias($name, $to, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_alias", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $to));
    }
    method add_my_stash($name, $pkg, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_my_stash", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $pkg));
    }
    method add_my_sub($name, $body, :$file, :$line, :$pos) {
        self._addlex_result(downcall("add_my_sub", self, ~$name,
            ~($file//''), +($line//0), +($pos// -1), $body));
    }

    method finish($ops, $done?) {
        $ops := $PassSimplifier.invoke_incr(self, $ops) unless $done;
        self.set_extend('onlystub', True) if $ops.onlystub;
        Q:CgOp { (cb_finish {self} {$ops.cgop(self)}) }
    }

    # helper for compile_get_pkg; handles stuff like SETTING::OUTER::Foo,
    # recursively.
    method _lexy_ref(*@names, :$auto) {
        @names || die "Cannot use a lexical psuedopackage as a compile time package reference";
        self // die "Passed top of lexical tree";
        given shift @names {
            when 'OUTER'   { return self.outer._lexy_ref(@names, :$auto) }
            when 'SETTING' { return self.to_unit.outer._lexy_ref(@names, :$auto) }
            when 'UNIT'    { return self.to_unit._lexy_ref(@names, :$auto) }
            when 'CALLER'  { die "Cannot use CALLER in a compile time name" }
            default {
                my @lex = self.lookup_lex($_);
                @lex || die "No lexical found for $_";
                @lex[0] eq 'package' || die "Lexical $_ is not a package";
                return $*unit.rel_pkg(@lex[4], @names, :$auto);
            }
        }
    }

    method true_setting() {
        my $c = self;
        $c = $c.to_unit.outer while $c.unit.name ne 'CORE';
        $c;
    }

    # returns direct reference to package, or dies
    method compile_get_pkg(*@names, :$auto) {
        @names || die "Cannot make a compile time reference to the semantic root package";
        my $n0 = shift(@names);
        if $n0 eq 'OUR' {
            return $*unit.rel_pkg(self.cur_pkg, @names, :$auto);
        } elsif $n0 eq 'PROCESS' or $n0 eq 'GLOBAL' {
            return $*unit.abs_pkg($n0, @names, :$auto);
        } elsif $n0 eq any < COMPILING DYNAMIC CLR CALLER > {
            # Yes, COMPILING is right here.  Because COMPILING is only valid
            # when recursively running code within the compiler, but this
            # function is only called directly from the compiler.  The closest
            # it comes to making sense is if you use eval in a macro.  Don't
            # do that, okay?
            die "Pseudo package $n0 may not be used in compile time reference";
        } elsif $n0 eq 'MY' {
            return self._lexy_ref(@names, :$auto);
        } elsif $n0 eq 'CORE' {
            return self.true_setting._lexy_ref(@names, :$auto);
        } elsif $n0 eq 'OUTER' or $n0 eq 'SETTING' or $n0 eq 'UNIT' {
            return self._lexy_ref($n0, @names, :$auto);
        } elsif $n0 ne 'PARENT' && self.lookup_lex($n0) {
            return self._lexy_ref($n0, @names, :$auto);
        } elsif $n0 ~~ /^\W/ {
            return $*unit.rel_pkg(self.cur_pkg, $n0, @names, :$auto);
        } else {
            return $*unit.abs_pkg('GLOBAL', $n0, @names, :$auto);
        }
    }
}


class Type {
    method FALLBACK($name, *@args) { downcall("type_$name", self, @args) }

    method add_method($mode, $name, $sub, :$file, :$line, :$pos) {
        downcall("type_add_method", self, $mode, $name, $sub,
            $file, $line, $pos);
    }
    method add_attribute($name, $sigil, $access, $type, :$file, :$line, :$pos) {
        downcall("type_add_attribute", self, $name, $sigil, ?$access, $type,
            $file, $line, $pos);
    }
}

class Unit {
    method FALLBACK($name, *@args) { downcall("unit_$name", self, @args) }
    method abs_pkg(*@names, :$auto) {
        downcall("unit_rel_pkg", self, ?$auto, Any, @names)
    }
    method rel_pkg($pkg, *@names, :$auto) {
        downcall("unit_rel_pkg", self, ?$auto, $pkg, @names)
    }
    method bind($pkg, $name, $item, :$file, :$line, :$pos) { #OK
        downcall("unit_bind", self, ~$pkg, ~$name, $item, ~($file // '???'),
            $line // 0);
    }

    method create_type(:$name, :$class, :$who) {
        downcall("type_create", self, ~$name, ~$class, ~$who);
    }
    method create_sub(:$name, :$class, :$outer, :$cur_pkg, :$in_class,
            :$run_once, :$outer_frame) {
        downcall("create_sub", self, ~($name // 'ANON'), $outer,
            ~($class // 'Sub'), $cur_pkg, $in_class, ?$run_once, $outer_frame)
    }
}

method push_compartment() {
    my \c = downcall("push_compartment");
    Q:CgOp { (rnull (cb_init_compartment {c})) };
    c;
}
method pop_compartment($c) { downcall("pop_compartment", $c) }
method get_codepoint($str) { downcall("get_codepoint", $str) }

method create_unit($comph, $name, $filename, $source, $main, $run) {
    downcall("new_unit", $comph, ~$name, ~$filename, ~$source, ?$main, ?$run);
}
