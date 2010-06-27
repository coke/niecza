namespace Sprixel {
    // We like to reuse continuation objects for speed - every function only
    // creates one kind of continuation, but tweaks a field for exact return
    // point.  As such, call frames and continuations are in 1:1 correspondence
    // and are unified.  Functions take a current continuation and return a new
    // continuation; we tail recurse with trampolines.

    // Only call other functions in Continue, not in the CallableDelegate or
    // equivalent!
    public delegate Frame CallableDelegate(Frame caller,
            LValue pos[], Dictionary<string, LValue> named);
    // Used by DynFrame to plug in code
    public delegate void DynBlockDelegate(DynamicFrame frame);

    public interface IP6 {
        public Frame Invoke(Frame caller, LValue pos[],
                Dictionary<string, LValue> named);
        // include the invocant in the positionals!  it will not usually be
        // this, rather a container of this
        public Frame InvokeMethod(Frame caller, string name,
                LValue pos[], Dictionary<string, LValue> named);
        public Frame GetAttribute(Frame caller, string name);
        //public Frame WHERE(Frame caller);
        public Frame HOW(Frame caller);
    }

    public struct LValue {
        public IPerl6Object container;
        public bool rw;
    }

    public class Variable {
        public LValue lv;
        public bool bvalue;
    }

    // We need hashy frames available to properly handle BEGIN; for the time
    // being, all frames will be hashy for simplicity
    public class Frame: IP6 {
        public readonly Frame caller;
        public readonly Frame outer;
        public IP6 resultSlot = null;
        public int ip = 0;
        public readonly DynBlockDelegate code;
        public readonly Dictionary<string, IP6> lex
            = new Dictionary<string, IP6>;

        public Frame(Frame outer_) : this(null, outer_, null) {}

        public Frame(Frame caller_, Frame outer_,
                DynBlockDelegate code_) {
            caller = caller_;
            outer = outer_;
            code = code_;
        }

        public Frame Continue() {
            return code(this);
        }

        public Frame GetSlot(Frame c, string name) {
            c.resultSlot = lex[name];
        }
    }

    public class ExceptionHelper: Frame {
        private Frame cursor;
        private LValue toThrowPos[];
        private Dictionary<string, LValue> toThrowNamed;

        private ExceptionHelper(Frame caller_) { caller = caller_; }

        public static Frame Throw(Frame caller, LValue pos[],
                Dictionary<string, LValue> named) {
            var n = new ExceptionHelper();
            n.cursor = caller;
            n.toThrowPos = pos;
            n.toThrowNamed = named;
            return n;
        }

        public Frame Continue() {
            switch (ip) {
                case 0:
                    if (cursor == null) {
                        throw new Exception("Unhandled Perl 6 exception");
                    }
                    ip = 1;
                    resultSlot = null;
                    return cursor.GetAttribute(this, "!exn_skipto");
                case 1:
                    // if skipto, skip some frames.  Used to implement CATCH
                    // invisibility
                    if (resultSlot != null) {
                        cursor = (Frame)resultSlot;
                        goto case 0;
                    }
                    ip = 2;
                    resultSlot = null;
                    return cursor.GetAttribute(this, "!exn_handler");
                case 2:
                    if (resultSlot != null) {
                        return ((IPerl6Object)resultSlot).invoke(caller,
                                toThrowPos, toThrowNamed);
                    }
                    cursor = cursor.caller;
                    goto case 0;
            }
        }
    }

    // This is quite similar to DynFrame and I wonder if I can unify them.
    // These are always hashy for the same reason as Frame above
    public class DynObject: IP6 {
        public Dictionary<string, IP6> slots;
        public Dictionary<string, IP6> methods;
        public IP6 how;

        public Frame InvokeMethod(Frame caller, string name,
                LValue pos[], Dictionary<string, LValue> named) {
            IP6 m = methods[name];
            if (m != null) {
                // XXX this breaks the static call nesting rule; does it need
                // to be rewritten or can the rule be safely loosened?
                return m.Invoke(caller, pos, named);
            } else {
                return Sprixel.Callout.InvokeFailed(caller, pos, named);
            }
        }

        public Frame Invoke(Frame caller, LValue pos[],
                Dictionary<string, LValue> named) {
            IP6 d = slots["clr-delegate"];
            if (d != null) {
                return (Sprixel.CallableDelegate)(((CLRImportObject)d).val)
                    (caller, pos, named);
            } else {
                // TODO: throw exception
            }
        }

        public Frame GetAttribute(Frame caller, string name) {
            caller.resultSlot = slots[name];
            return caller;
        }

        public Frame HOW(Frame caller) {
            caller.resultSlot = how;
            return caller;
        }
    }

    // Allows native CLR objects to be treated as Perl 6 data.  They don't
    // currently support any operations; you'll need to use CLR code to work
    // with them.
    public class CLRImportObject : IP6 {
        public readonly object val;
    }

    // A bunch of stuff which raises big circularity issues if done in the
    // setting itself.
    // Provides: ClassHOW, ClassHOW.HOW, ClassHOW.add_method, ScalarContainer,
    // ScalarContainer.HOW, Code, Code.HOW, Body, Body.HOW, Scope, Scope.HOW,
    // ...
    // This should be enough to implement the rest of ClassHOW :)
    public class KernelSetting {
        public static readonly IP6 KernelFrame;
        public static readonly IP6 KernelScope;
    }

    public class MainClass {
        public static void Main() {
            Frame root_f = new Frame(null, null, new DynBlockDelegate(R.Continue));
            Frame current = root_f;
            while (current) {
                current = current.Continue();
            }
        }

        // bootstrap function for the compilation unit.  runs phasers, sets up
        // runtime meta objects at BEGIN, then calls the mainline
        private class R {
            public static Frame Continue(Frame th) {
                switch (th.ip) {
                    case 0:
                        // BEGIN
                        Frame main_f = new Frame(KernelSetting.KernelFrame);
                        IPerl6Object main_s = KernelSetting.MakeSub(
                            new DynBlockDelegate(B.Continue), main_f,
                            KernelSetting.KernelFrame);
                        // CHECK
                        // INIT
                        // DO
                        // could optimize this quite a bit since the mainline
                        // and setting both only run once.  For later.
                        IPerl6Object main_c = KernelSetting.CloneSub(
                            main_s, KernelSetting.KernelFrame);
                        th.ip = 1;
                        return main_c.Invoke(th, new LValue[0], null);
                    case 1:
                        return th.caller;
                }
                return null;
            }
        }

        private class B {
            public static Frame Continue(Frame th) {
                LValue c;
                IP6 d;
                switch (th.ip) {
                    case 0:
                        th.ip = 1;
                        th.resultSlot = null;
                        c = th.outer.slots["&say"].lv;
                        return c.container.InvokeMethod("FETCH",
                                new LValue[1] { c }, null);
                    case 1:
                        c = KernelSetting.MakeStrObject("Hello, World");
                        d = th.resultSlot;
                        th.ip = 2;
                        th.resultSlot = null;
                        return d.Invoke(new LValue[1] { c }, null);
                    case 2:
                        return th.caller;
                }
            }
        }
    }
}
