using System;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Niecza.CLRBackend;
using Niecza.Serialization;

namespace Niecza {
    // We like to reuse continuation objects for speed - every function only
    // creates one kind of continuation, but tweaks a field for exact return
    // point.  As such, call frames and continuations are in 1:1 correspondence
    // and are unified.  Functions take a current continuation and return a new
    // continuation; we tail recurse with trampolines.

    // Used by DynFrame to plug in code
    public delegate Frame DynBlockDelegate(Frame frame);

    public sealed class DispatchEnt : IFreeze {
        public DispatchEnt next;
        public SubInfo info;
        public Frame outer;
        public P6any ip6;

        public DispatchEnt() {}
        public DispatchEnt(DispatchEnt next, P6any ip6) {
            this.ip6 = ip6;
            this.next = next;
            P6opaque d = (P6opaque)ip6;
            this.outer = (Frame) d.slots[0];
            this.info = (SubInfo) d.slots[1];
        }

        void IFreeze.Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.DispatchEnt);
            fb.ObjRef(next);
            fb.ObjRef(info);
            fb.ObjRef(outer);
            fb.ObjRef(ip6);
        }
        internal static DispatchEnt Thaw(ThawBuffer tb) {
            DispatchEnt de = new DispatchEnt();
            tb.Register(de);
            de.next = (DispatchEnt) tb.ObjRef();
            de.info = (SubInfo) tb.ObjRef();
            de.outer = (Frame) tb.ObjRef();
            de.ip6 = (P6any) tb.ObjRef();
            return de;
        }
    }

    // A Variable is the meaning of function arguments, of any subexpression
    // except the targets of := and ::=.

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public abstract class Variable : IFreeze {
        public ViviHook whence;

        // these should be treated as ro for the life of the variable
        public STable type;
        public bool rw;
        public bool islist;

        public abstract P6any Fetch();
        public abstract void Store(P6any v);

        public abstract Variable GetVar();

        public abstract void Freeze(FreezeBuffer fb);

        // note: callers need to make sure type is set up properly if null
        public Variable() { }

        public static readonly Variable[] None = new Variable[0];
    }

    public abstract class ViviHook : IFreeze {
        public abstract void Freeze(FreezeBuffer fb);
        public abstract void Do(Variable toviv);
    }

    public class SubViviHook : ViviHook {
        P6any sub;
        public SubViviHook(P6any sub) { this.sub = sub; }
        public override void Do(Variable toviv) {
            Kernel.RunInferior(sub.Invoke(Kernel.GetInferiorRoot(),
                        new Variable[] { toviv }, null));
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.SubViviHook);
            fb.ObjRef(sub);
        }
        internal static SubViviHook Thaw(ThawBuffer tb) {
            return new SubViviHook((P6any)tb.ObjRef());
        }
    }

    public class HashViviHook : ViviHook {
        P6any hash;
        string key;
        public HashViviHook(P6any hash, string key) { this.hash = hash; this.key = key; }
        public override void Do(Variable toviv) {
            VarHash rh = Kernel.UnboxAny<VarHash>(hash);
            rh[key] = toviv;
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.HashViviHook);
            fb.ObjRef(hash);
            fb.String(key);
        }
        internal static IFreeze Thaw(ThawBuffer tb) {
            return new HashViviHook((P6any)tb.ObjRef(), tb.String());
        }
    }

    public class NewHashViviHook : ViviHook {
        Variable hashv;
        string key;
        public NewHashViviHook(Variable hashv, string key) { this.hashv = hashv; this.key = key; }
        public override void Do(Variable toviv) {
            VarHash rh = new VarHash();
            rh[key] = toviv;
            hashv.Store(Kernel.BoxRaw(rh, Kernel.HashMO));
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.NewHashViviHook);
            fb.ObjRef(hashv);
            fb.String(key);
        }
        internal static IFreeze Thaw(ThawBuffer tb) {
            return new NewHashViviHook((Variable)tb.ObjRef(), tb.String());
        }
    }

    public class ArrayViviHook : ViviHook {
        P6any ary;
        int key;
        public ArrayViviHook(P6any ary, int key) { this.ary = ary; this.key = key; }
        public override void Do(Variable toviv) {
            VarDeque vd = (VarDeque) ary.GetSlot("items");
            while (vd.Count() <= key)
                vd.Push(Kernel.NewTypedScalar(null));
            vd[key] = toviv;
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.ArrayViviHook);
            fb.ObjRef(ary);
            fb.Int(key);
        }
        internal static IFreeze Thaw(ThawBuffer tb) {
            return new ArrayViviHook((P6any)tb.ObjRef(), tb.Int());
        }
    }

    public class NewArrayViviHook : ViviHook {
        Variable ary;
        int key;
        public NewArrayViviHook(Variable ary, int key) { this.ary = ary; this.key = key; }
        public override void Do(Variable toviv) {
            VarDeque vd = new VarDeque();
            while (vd.Count() <= key)
                vd.Push(Kernel.NewTypedScalar(null));
            vd[key] = toviv;
            P6opaque d = new P6opaque(Kernel.ArrayMO);
            d.slots[0] = vd;
            d.slots[1] = new VarDeque();
            ary.Store(d);
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.NewArrayViviHook);
            fb.ObjRef(ary);
            fb.Int(key);
        }
        internal static IFreeze Thaw(ThawBuffer tb) {
            return new NewArrayViviHook((Variable)tb.ObjRef(), tb.Int());
        }
    }

    public sealed class SimpleVariable: Variable, IFixup {
        P6any val;

        private SimpleVariable() { }
        public SimpleVariable(bool rw, bool islist, STable type, ViviHook whence, P6any val) {
            this.val = val; this.whence = whence; this.rw = rw;
            this.islist = islist; this.type = type;
        }

        public override P6any  Fetch()       { return val; }
        public override void Store(P6any v)  {
            if (!rw) {
                throw new NieczaException("Writing to readonly scalar");
            }
            if (v == Kernel.NilP) {
                v = type.initObject;
            }
            if (!v.Does(type)) {
                throw new NieczaException("Nominal type check failed for scalar store; got " + v.mo.name + ", needed " + type.name + " or subtype");
            }
            if (whence != null) {
                ViviHook vh = whence;
                whence = null;
                vh.Do(this);
            }
            val = v;
        }

        public override Variable GetVar() {
            return Kernel.BoxAnyMO<Variable>(this, Kernel.ScalarMO);
        }

        const int S_RO   = 0;
        const int S_LIST = 1;
        const int S_RW   = 2;
        const int S_VIV  = 3;

        public override void Freeze(FreezeBuffer fb) {
            int code = ((int)SerializationCode.SimpleVariable) +
                (islist ? S_LIST : !rw ? S_RO : whence == null ? S_RW : S_VIV);
            fb.Byte((byte)code);

            if (whence != null) fb.ObjRef(whence);
            if (rw) fb.ObjRef(type);
            fb.ObjRef(val);
        }
        void IFixup.Fixup() {
            type = val.mo;
        }
        internal static SimpleVariable Thaw(ThawBuffer tb, int subcode) {
            SimpleVariable n = new SimpleVariable();
            tb.Register(n);
            switch (subcode) {
                default: throw new ArgumentException(subcode.ToString());
                case S_RO:
                    // rw = false islist = false whence = null type = null
                    tb.PushFixup(n);
                    break;
                case S_LIST:
                    tb.PushFixup(n);
                    n.islist = true;
                    break;
                case S_VIV:
                    n.whence = (ViviHook) tb.ObjRef();
                    goto case S_RW;
                case S_RW:
                    n.rw = true;
                    n.type = (STable) tb.ObjRef();
                    break;
            }
            n.val = (P6any) tb.ObjRef();
            return n;
        }
    }

    public sealed class TiedVariable: Variable {
        P6any fetch;
        P6any store;

        private TiedVariable() { }
        public TiedVariable(STable type, P6any whsub, P6any fetch, P6any store) {
            this.fetch = fetch;
            this.store = store;
            this.whence = whsub.IsDefined() ? new SubViviHook(whsub) : null;
            this.rw = true;
            this.type = type;
        }

        public override P6any Fetch() {
            Variable vr = Kernel.RunInferior(fetch.Invoke(
                Kernel.GetInferiorRoot(), None, null));
            P6any vl = vr.Fetch();
            if (!vl.Does(type))
                throw new NieczaException("Tied variable of type " + type +
                        " returned value of type " + vl.mo.name + " instead");
            return vl;
        }

        public override void Store(P6any v) {
            if (whence != null) {
                ViviHook vh = whence;
                whence = null;
                vh.Do(this);
            }
            Kernel.RunInferior(store.Invoke(Kernel.GetInferiorRoot(),
                        new Variable[] { Kernel.NewROScalar(v) }, null));
        }

        public override Variable GetVar() {
            return Kernel.BoxAnyMO<Variable>(this, Kernel.ScalarMO);
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.TiedVariable);
            fb.ObjRef(type);
            fb.ObjRef(fetch);
            fb.ObjRef(store);
            fb.ObjRef(whence);
        }
        internal static TiedVariable Thaw(ThawBuffer tb) {
            TiedVariable n = new TiedVariable();
            tb.Register(n);
            n.type   = (STable)   tb.ObjRef();
            n.fetch  = (P6any)    tb.ObjRef();
            n.store  = (P6any)    tb.ObjRef();
            n.whence = (ViviHook) tb.ObjRef();
            return n;
        }
    }

    // Used to make Variable sharing explicit in some cases; will eventually be
    // the only way to share a bvalue
    public sealed class BValue {
        public Variable v;
        public BValue(Variable v) { this.v = v; }
    }

    public class StashEnt : IFreeze {
        public Variable v;
        public string   file;
        public int      line;

        void IFreeze.Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.StashEnt);
            fb.ObjRef(v);
            fb.String(file);
            fb.Int(line);
        }

        internal static StashEnt Thaw(ThawBuffer tb) {
            StashEnt r = new StashEnt();
            tb.Register(r);
            r.v = (Variable)tb.ObjRef();
            r.file = tb.String();
            r.line = tb.Int();
            return r;
        }
    }

    // set this on mutable global fields to indicate that they should be saved
    // along with CORE.  Not really needed for immutable fields
    [AttributeUsage(AttributeTargets.Field)]
    class CORESavedAttribute : Attribute { }

    public sealed class RuntimeUnit : IFreeze {
        public string name, filename, modtime, asm_name, dll_name;
        public Dictionary<string, StashEnt> globals;
        public SubInfo mainline, bottom;
        public bool is_mainish;
        public Type type;
        public List<SubInfo> our_subs;

        // used during construction only
        public AssemblyBuilder asm_builder;
        public ModuleBuilder mod_builder;
        public TypeBuilder type_builder;
        public List<KeyValuePair<int,STable>> stubbed_stashes;
        public Dictionary<object, FieldBuilder> constants;
        internal Dictionary<string, CpsOp> val_constants;
        public int nextid;

        class IdentityComparer : IEqualityComparer<object> {
            public int GetHashCode(object o) {
                return RuntimeHelpers.GetHashCode(o);
            }
            public new bool Equals(object a, object b) {
                return a == b;
            }
        }

        private RuntimeUnit() { }

        public RuntimeUnit(string name, string filename, string modtime,
                string obj_dir, bool main, bool runnow) {
            this.name = name;
            this.filename = filename;
            this.modtime = modtime;
            this.globals = new Dictionary<string,StashEnt>();
            this.stubbed_stashes = new List<KeyValuePair<int,STable>>();
            this.is_mainish = main;
            if (name == "CORE")
                Kernel.CreateBasicTypes();

            this.asm_name = name.Replace("::", ".");
            this.dll_name = asm_name + (main ? ".exe" : ".dll");
            our_subs = new List<SubInfo>();
        }

        void GenerateCode(bool runnow) {
            this.asm_builder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                    new AssemblyName(asm_name),
                    (runnow ? AssemblyBuilderAccess.Run :
                        AssemblyBuilderAccess.Save),
                    AppDomain.CurrentDomain.BaseDirectory);
            mod_builder = runnow ? asm_builder.DefineDynamicModule(asm_name) :
                asm_builder.DefineDynamicModule(asm_name, dll_name);
            //mod_builder = asm_builder.DefineDynamicModule(asm_name, dll_name);

            type_builder = mod_builder.DefineType(asm_name,
                    TypeAttributes.Public | TypeAttributes.Sealed |
                    TypeAttributes.Abstract | TypeAttributes.Class |
                    TypeAttributes.BeforeFieldInit);

            constants = new Dictionary<object,FieldBuilder>(new IdentityComparer());
            val_constants = new Dictionary<string,CpsOp>();
            NamProcessor[] ths = new NamProcessor[our_subs.Count];
            for (int i = 0; i < ths.Length; i++) {
                SubInfo z = our_subs[i];
                if (Config.CGVerbose > 0)
                    Console.WriteLine("generating code for: {0}", z.name);
                ths[i] = new NamProcessor(
                    new CpsBuilder(this, "C" + i + z.name, true), z);
                ths[i].MakeBody(Reader.Read(z.nam_str, z.nam_refs));
            }

            type = type_builder.CreateType();

            for (int i = 0; i < ths.Length; i++) {
                ths[i].FillSubInfo(type);
            }
        }

        internal static ObjectRegistry reg = new ObjectRegistry();

        public void Save() {
            GenerateCode(false);
            asm_builder.Save(dll_name);
            reg.SaveUnit(asm_name, this);
        }

        public void PrepareEval() {
            GenerateCode(true);

            foreach (SubInfo z in our_subs)
                if ((z.special & SubInfo.UNSAFE) != 0)
                    Kernel.CheckUnsafe(z);

            foreach (KeyValuePair<object, FieldBuilder> kv in constants)
                type.GetField(kv.Value.Name).SetValue(null, kv.Key);

            if (Environment.GetEnvironmentVariable("NIECZA_DEFER_TRACE") != null) {
                Kernel.TraceFlags = Kernel.TRACE_CUR;
                Kernel.TraceCount = Kernel.TraceFreq = 1;
            }

            Kernel.FirePhasers(this, Kernel.PHASER_UNIT_INIT, false);
            Kernel.FirePhasers(this, Kernel.PHASER_INIT, false);
        }

        internal CpsOp TypeConstant(STable s) {
            return RefConstant(s == null ? "" : s.name, s, Tokens.STable);
        }
        internal CpsOp SubConstant(SubInfo s) {
            return RefConstant(s == null ? "" : s.name, s, Tokens.SubInfo);
        }

        internal CpsOp RefConstant(string name, object val, Type nty) {
            if (val == null)
                return CpsOp.Null(nty);
            FieldBuilder fi;
            if (!constants.TryGetValue(val, out fi))
                constants[val] = fi = NewField(name, val is Variable ? typeof(Variable) : val.GetType());
            return CpsOp.IsConst(CpsOp.GetSField(fi));
        }

        internal CpsOp ValConstant(string desc, object val) {
            CpsOp r;
            if (!val_constants.TryGetValue(desc, out r))
                val_constants[desc] = r =
                    RefConstant(desc.Substring(0, desc.Length > 20 ? 20 : desc.Length), val, null);
            return r;
        }

        internal CpsOp VarConstStr(string s) {
            return ValConstant('S' + s, Builtins.MakeStr(s));
        }

        internal CpsOp VarConstNum(double n) {
            char[] c = new char[5];
            c[0] = 'N';
            long b = BitConverter.DoubleToInt64Bits(n);
            c[1] = (char)(b);
            c[2] = (char)(b >> 16);
            c[3] = (char)(b >> 32);
            c[4] = (char)(b >> 48);
            return ValConstant(new string(c), Builtins.MakeFloat(n));
        }

        public Variable ExactNum(int numbase, string digits) {
            BigInteger num = BigInteger.Zero;
            BigInteger den = BigInteger.Zero;

            bool neg = false;

            foreach (char d in digits) {
                if (d == '-') neg = true;
                if (d == '_') continue;
                if (d == '.') {
                    if (den != BigInteger.Zero)
                        throw new Exception("two dots in " + digits);
                    den = BigInteger.One;
                    continue;
                }
                int digval;
                if (d >= '0' && d <= '9') { digval = d - '0'; }
                else if (d >= 'a' && d <= 'z') { digval = d + 10 - 'a'; }
                else if (d >= 'A' && d <= 'Z') { digval = d + 10 - 'A'; }
                else { throw new Exception("invalid digit in " + digits); }

                if (digval >= numbase) { throw new Exception("out of range digit in " + digits); }

                num *= numbase;
                den *= numbase;
                num += digval;
            }

            if (neg) num = -num;

            if (num == BigInteger.Zero && den.Sign > 0)
                den = BigInteger.One;
            if (den > BigInteger.One) {
                BigInteger g = BigInteger.GreatestCommonDivisor(num, den);
                if (g != BigInteger.One) {
                    num /= g;
                    den /= g;
                }
            }

            ulong sden;
            if (!den.AsUInt64(out sden)) {
                return Builtins.MakeFloat((double)num / (double)den);
            }

            if (sden == 0)
                return Builtins.MakeInt(num);

            return Builtins.MakeFixRat(num, sden);
        }

        internal CpsOp VarConstExact(int bas, string digs) {
            return ValConstant("X" + bas + ',' + digs, ExactNum(bas, digs));
        }

        internal CpsOp CCConst(int[] cc) {
            StringBuilder code = new StringBuilder("CC");
            foreach (int x in cc) {
                code.Append((char)x);
                code.Append((char)(x>>16));
            }
            return ValConstant(code.ToString(), new CC(cc));
        }

        internal CpsOp StringListConst(string[] sl) {
            StringBuilder code = new StringBuilder("SL");
            foreach (string s in sl) {
                code.Append((char)(s.Length >> 16));
                code.Append((char)(s.Length));
                code.Append(s);
            }
            return ValConstant(code.ToString(), sl);
        }

        internal CpsOp CCListConst(int[][] ccl) {
            StringBuilder code = new StringBuilder("CL");
            foreach (int[] cc in ccl) {
                code.Append((char)(cc.Length >> 16));
                code.Append((char)(cc.Length));
                foreach (int x in cc) {
                    code.Append((char)x);
                    code.Append((char)(x>>16));
                }
            }
            CC[] buf = new CC[ccl.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = new CC(ccl[i]);
            return ValConstant(code.ToString(), buf);
        }

        internal CpsOp AltInfoConst(CgContext cx, object lads, string dba, string[] labels) {
            throw new NotImplementedException(); // TODO: redesign needed
        }

        internal FieldBuilder NewField(string name, Type ty) {
            string fname = name;
            int i = 1;
            while (type_builder.GetField(fname) != null)
                fname = name + (i++);
            return type_builder.DefineField(fname, ty, FieldAttributes.Public |
                    FieldAttributes.Static);
        }

        public static Variable MakeAppropriateVar(string name) {
            if (name.Length >= 1 && name[0] == '@')
                return Kernel.CreateArray();
            if (name.Length >= 1 && name[0] == '%')
                return Kernel.CreateHash();
            return Kernel.NewTypedScalar(null);
        }

        public static string NsMerge(string who, string name,
                ref StashEnt nse, StashEnt ose) {

            P6any nseo = nse.v.Fetch();
            P6any oseo = ose.v.Fetch();
            bool  nseod = nseo.IsDefined();
            bool  oseod = oseo.IsDefined();
            // lowest priority are empty common symbols
            if (nse.v.rw && !nseod) {
                nse = ose;
                return null;
            }
            if (ose.v.rw && !oseod) {
                return null;
            }

            // no conflict if items are identical
            if (!ose.v.rw && !nse.v.rw && oseo == nseo) {
                nse = ose;
                return null;
            }

            // no conflict if items are simple packages with the same who
            if (!ose.v.rw && !oseod && oseo.mo.mo.isPackage &&
                    !nse.v.rw && !nseod && nseo.mo.mo.isPackage &&
                    oseo.mo.who.Isa(Kernel.StashMO) &&
                    nseo.mo.who.Isa(Kernel.StashMO) &&
                    Kernel.UnboxAny<string>(oseo.mo.who) ==
                        Kernel.UnboxAny<string>(nseo.mo.who)) {
                nse = ose;
                return null;
            }

            return "Two definitions found for symbol "+who+"::"+name+"\n\n" +
                    "  first at "+ose.file+" line "+ose.line+"\n" +
                    "  second at "+nse.file+" line "+nse.line;
        }

        public string NsBind(string who, string name, Variable var,
                string file, int line) {
            string key = (char)who.Length + who + name;
            StashEnt nse = new StashEnt();
            if (var == null)
                var = MakeAppropriateVar(name);
            nse.v = var;
            nse.file = file;
            nse.line = line;
            StashEnt ose;
            string err = null;
            if (globals.TryGetValue(key, out ose))
                err = NsMerge(who, name, ref nse, ose);
            if (err != null) return err;
            globals[key] = nse;
            return null;
        }

        void IFreeze.Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.RuntimeUnit);

            fb.String(name);
            fb.String(filename);
            fb.String(modtime);
            fb.String(asm_name);
            fb.String(dll_name);

            fb.Int(globals.Count);
            foreach (KeyValuePair<string, StashEnt> kv in globals) {
                fb.String(kv.Key);
                fb.ObjRef(kv.Value);
            }

            fb.ObjRef(mainline);
            fb.ObjRef(bottom);
            fb.Refs(our_subs);
            fb.Byte((byte)(is_mainish ? 1 : 0));

            if (name == "CORE") {
                FieldInfo[] kf = typeof(Kernel).GetFields();
                Array.Sort<FieldInfo>(kf,
                        (f1, f2) => string.CompareOrdinal(f1.Name, f2.Name));
                foreach (FieldInfo f in kf) {
                    if (f.GetCustomAttributes(typeof(CORESavedAttribute), true).Length != 0) {
                        fb.ObjRef(f.GetValue(null));
                    }
                }
            }
        }

        internal static RuntimeUnit Thaw(ThawBuffer tb) {
            RuntimeUnit n = new RuntimeUnit();
            tb.Register(n);

            n.name     = tb.String();
            n.filename = tb.String();
            n.modtime  = tb.String();
            n.asm_name = tb.String();
            n.dll_name = tb.String();

            int ct = tb.Int();
            n.globals = new Dictionary<string, StashEnt>();
            for (int i = 0; i < ct; i++) {
                n.globals[tb.String()] = (StashEnt)tb.ObjRef();
            }

            n.mainline   = (SubInfo)tb.ObjRef();
            n.bottom     = (SubInfo)tb.ObjRef();
            n.our_subs   = tb.RefsL<SubInfo>();
            n.is_mainish = tb.Byte() != 0;

            if (n.name == "CORE") {
                FieldInfo[] kf = typeof(Kernel).GetFields();
                Array.Sort<FieldInfo>(kf,
                        (f1, f2) => string.CompareOrdinal(f1.Name, f2.Name));
                foreach (FieldInfo f in kf) {
                    if (f.GetCustomAttributes(typeof(CORESavedAttribute), true).Length != 0) {
                        f.SetValue(null, tb.ObjRef());
                    }
                }
            }
            return n;
        }
    }

    public sealed class LeaveHook {
        public LeaveHook next;
        public P6any     thunk;
        public int       type;

        public const int KEEP = 1;
        public const int UNDO = 2;
        public const int DIE  = 4;
        public const int POST = 7;
        public const int SENTINEL = 15;
    }

    public abstract class LexInfo {
        public SubInfo owner;
        public string name;
        public string file;
        public int line;
        public int pos;

        public abstract void Init(Frame f);
        public abstract object Get(Frame f);
        public virtual void Set(Frame f, object to) {
            throw new NieczaException("Variable cannot be bound");
        }

        internal virtual ClrOp SetCode(int up, ClrOp head) {
            throw new Exception("Lexicals of type " + this + " cannot be bound");
        }
        internal abstract ClrOp GetCode(int up);

        // names that are forced to dynamism for quick access
        public static bool IsDynamicName(string name) {
            if (name == "$_" || name == "$/" || name == "$!") return true;
            if (name.Length < 2) return false;
            if (name[0] == '*' || name[0] == '?') return true;
            if (name[1] == '*' || name[1] == '?') return true;
            return false;
        }
        public virtual void BindFields() {}
        public virtual int SigIndex() { return -1; }

        internal abstract void DoFreeze(FreezeBuffer fb);
        internal enum LexSerCode {
            Simple, Sub, Label, Dispatch, Common, Hint, Package, Alias
        }
    }

    public abstract class LIVarish : LexInfo {
        public int index;

        public LIVarish() { }

        public override int SigIndex() { return index; }
        public override void BindFields() {
            index = owner.num_lex_slots++;
            if (owner.protopad != null) {
                int osz = owner.protopad.lexn == null ? 0 : owner.protopad.lexn.Length;
                if (owner.num_lex_slots > 10 + osz)
                    Array.Resize(ref owner.protopad.lexn, owner.num_lex_slots * 2 - 10);
            }
        }

        public override object Get(Frame f) {
            return f.GetDynamic(index);
        }

        public override void Set(Frame f, object to) {
            f.SetDynamic(index, to);
        }

        internal override ClrOp GetCode(int up) {
            if ((owner.special & SubInfo.RUN_ONCE) != 0) {
                return new ClrUnboxAny(Tokens.Variable,
                        new ClrMethodCall(false, Tokens.Frame.GetMethod("GetDynamic"),
                    Backend.currentUnit.RefConstant(
                        owner.name, owner.protopad, typeof(Frame)).head,
                    new ClrIntLiteral(typeof(int), index)));
                //return new ClrProtoGet(index,
                //    Backend.currentUnit.RefConstant(
                //        owner.name, owner.protopad, typeof(Frame)).head);
            }
            return new ClrPadGet(up, index);
        }

        internal override ClrOp SetCode(int up, ClrOp to) {
            if ((owner.special & SubInfo.RUN_ONCE) != 0)
                return new ClrProtoSet(index,
                    Backend.currentUnit.RefConstant(
                        owner.name, owner.protopad, typeof(Frame)).head, to);
            return new ClrPadSet(up, index, to);
        }
    }

    // TODO: Provide some way to cache a StashEnt once the currentGlobals
    // stops changing
    public class LICommon : LexInfo {
        public readonly string hkey;
        public LICommon(string hkey) { this.hkey = hkey; }

        public override void Init(Frame f) { }
        internal string Stash() { return hkey.Substring(1, (int)hkey[0]); }
        internal string VarName() { return hkey.Substring(1 + (int)hkey[0]); }

        public override object Get(Frame f) {
            return Kernel.currentGlobals[hkey].v;
        }

        public override void Set(Frame f, object to) {
            Kernel.currentGlobals[hkey].v = (Variable)to;
        }

        internal override ClrOp GetCode(int up) {
            return new ClrMethodCall(false, Tokens.Kernel_GetGlobal,
                    new ClrStringLiteral(hkey));
        }

        internal override ClrOp SetCode(int up, ClrOp to) {
            return new ClrMethodCall(false, Tokens.Kernel_BindGlobal,
                    new ClrStringLiteral(hkey), to);
        }

        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Common);
            fb.String(hkey);
        }
    }

    public class LIHint : LexInfo {
        public BValue var;
        public LIHint() { }
        public override void Init(Frame f) { }
        public override void BindFields() {
            var = new BValue(null);
        }

        public override object Get(Frame f) { return var.v; }
        public override void Set(Frame f, object to) { var.v = (Variable)to; }

        internal override ClrOp GetCode(int up) {
            return new ClrGetField(Tokens.BValue_v,
                Backend.currentUnit.RefConstant(name, var, null).head);
        }

        internal override ClrOp SetCode(int up, ClrOp to) {
            return new ClrSetField(Tokens.BValue_v,
                Backend.currentUnit.RefConstant(name, var, null).head, to);
        }

        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Hint);
        }
    }

    public class LISub : LIVarish {
        public SubInfo def;
        public LISub(SubInfo def) { this.def = def; }
        internal LISub(int index, SubInfo def) { this.index = index; this.def = def; }
        public override void Init(Frame f) {
            Set(f, Kernel.NewROScalar(def.protosub));
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Sub);
            fb.Int(index);
            fb.ObjRef(def);
        }
    }

    public class LISimple : LIVarish {
        public const int NOINIT = 1;
        public const int ROINIT = 2;
        public const int DEFOUTER = 4;
        public const int LIST = 8;
        public const int HASH = 16;

        public int flags;
        public STable type;
        public LISimple(int flags, STable type) {
            this.flags = flags;
            this.type  = type;
        }
        internal LISimple(int index, int flags, STable type) {
            this.index = index;
            this.flags = flags;
            this.type  = type;
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Simple);
            fb.Int(index);
            fb.Byte((byte)flags);
            fb.ObjRef(type);
        }
        public override void Init(Frame f) {
            if ((flags & NOINIT) != 0)
                return;
            if ((flags & ROINIT) != 0)
                Set(f, Kernel.AnyMO.typeVar);
            else if ((flags & DEFOUTER) != 0)
                Set(f, f.info.GetOuterTopic(f));
            else if ((flags & LIST) != 0)
                Set(f, Kernel.CreateArray());
            else if ((flags & HASH) != 0)
                Set(f, Kernel.CreateHash());
            else
                Set(f, Kernel.NewTypedScalar(type));
        }
    }

    public class LILabel : LIVarish {
        public LILabel() { }
        internal LILabel(int index) { this.index = index; }
        public override void Init(Frame f) {
            Set(f, Kernel.NewLabelVar(f, name));
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Label);
            fb.Int(index);
        }
    }

    public class LIDispatch : LIVarish {
        public LIDispatch() { }
        internal LIDispatch(int index) { this.index = index; }
        public override void Init(Frame f) {
            throw new Exception("MMD NYI");
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Dispatch);
            fb.Int(index);
        }
    }

    public class LIAlias : LexInfo {
        public string to;
        public LIAlias(string to) { this.to = to; }
        public override void Init(Frame f) { }
        object Common(Frame f, bool set, object bind) {
            Frame cr = f;
            LexInfo li = null;
            while (cr.info.dylex == null ||
                    !cr.info.dylex.TryGetValue(to, out li))
                cr = cr.outer;
            if (!set) return li.Get(cr);
            else { li.Set(cr, bind); return null; }
        }
        public override object Get(Frame f) { return Common(f,false,null); }
        public override void Set(Frame f, object bind) { Common(f,true,bind); }

        internal override ClrOp GetCode(int up) {
            LexInfo real;
            SubInfo sc = owner;
            while (!sc.dylex.TryGetValue(to, out real)) {
                sc = sc.outer;
                up++;
            }
            return real.GetCode(up);
        }

        internal override ClrOp SetCode(int up, ClrOp bind) {
            LexInfo real;
            SubInfo sc = owner;
            while (!sc.dylex.TryGetValue(to, out real)) {
                sc = sc.outer;
                up++;
            }
            return real.SetCode(up, bind);
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Alias);
            fb.String(to);
        }
    }

    public class LIPackage : LexInfo {
        public STable pkg;
        public LIPackage(STable pkg) { this.pkg = pkg; }
        public override object Get(Frame f) { return pkg.typeVar; }
        public override void Init(Frame f) { }
        internal override ClrOp GetCode(int up) {
            return Backend.currentUnit.RefConstant(pkg.name + "V",
                    pkg.typeVar, typeof(Variable)).head;
        }
        internal override void DoFreeze(FreezeBuffer fb) {
            fb.Byte((byte)LexSerCode.Package);
            fb.ObjRef(pkg);
        }
    }

    // This stores all the invariant stuff about a Sub, i.e. everything
    // except the outer pointer.  Now distinct from protopads
    //
    // Actually not quite *in*variant; some of this stuff has to be
    // changed, but it's rare by construction.  We don't want to be
    // like Rakudo/Parrot where simple sub cloning requires copying
    // 100s of bytes.
    public class SubInfo : IFreeze, IFixup {
        // Essential call functions
        public DynBlockDelegate code;
        public int nspill;
        public int[] sig_i;
        public object[] sig_r;

        // Local metadata
        public int[] lines;
        public Dictionary<string, LexInfo> dylex;
        public uint dylex_filter; // (32,1) Bloom on hash code
        public string name;
        // maybe should be in extend or a hint?
        public LAD ltm;
        public int special;
        public int phaser = -1;
        public string outervar;

        // References to related objects
        public RuntimeUnit unit;
        public SubInfo outer;
        public P6any protosub;
        public Frame protopad;
        public STable cur_pkg, methodof, body_of, in_class;
        public STable mo;

        // caches for fast $OUTER::_, invocants, exn handling
        public int outer_topic_rank;
        public int outer_topic_key;
        public int self_key;
        public SubInfo catch_, control;

        // this is used only at compile time
        public Dictionary<string,UsedInScopeInfo> used_in_scope;
        public int num_lex_slots;

        // if this is non-null, compilation is being delayed
        public string nam_str;
        public object[] nam_refs;

        // Used for closing runtime-generated SubInfo over values used
        // For vtable wrappers: 0 = unboxed, 1 = boxed
        // For dispatch routines, 0 = parameter list
        public object[] param;
        public List<SubInfo> children = new List<SubInfo>();
        public Dictionary<string,object[]> extend;

        // No instance fields past this point
        public class UsedInScopeInfo {
            public string file;
            public int line;
            public int levels;
            public string orig_file;
            public int orig_line;
        }

        public const int RUN_ONCE = 1;
        public const int MAKE_PROTOPAD = 2;
        public const int HAS_TYPE = 4;
        public const int UNSAFE = 8;
        public const int PARAM_ROLE = 16;
        public const int MAINLINE = 32;
        public const int TRANSPARENT = 64;
        public const int INLINED = 128;
        public const int CANNOT_INLINE = 256;
        public const int RETURN_PASS = 512;

        public const int SIG_I_RECORD  = 3;
        public const int SIG_I_FLAGS   = 0;
        public const int SIG_I_SLOT    = 1;
        public const int SIG_I_NNAMES  = 2;

        // R records are variable size, but contain canonical name,
        // usable names (in order), default SubInfo (if present),
        // type STable (if present)

        // Value processing
        public const int SIG_F_HASTYPE    = 1; // else Kernel.AnyMO
        public const int SIG_F_MULTI_IGNORED = 16384;

        // Value binding
        public const int SIG_F_READWRITE  = 2;
        public const int SIG_F_RWTRANS    = 8;
        public const int SIG_F_BINDLIST   = 16;
        public const int SIG_F_INVOCANT   = 8192;
        public const int SIG_F_IS_COPY    = 32768;
        public const int SIG_F_IS_LIST    = 65536;
        public const int SIG_F_IS_HASH    = 131072;

        // Value source
        public const int SIG_F_HASDEFAULT = 32;
        public const int SIG_F_OPTIONAL   = 64;
        public const int SIG_F_DEFOUTER   = 4096;
        public const int SIG_F_POSITIONAL = 128;
        public const int SIG_F_SLURPY_POS = 256;
        public const int SIG_F_SLURPY_NAM = 512;
        public const int SIG_F_SLURPY_CAP = 1024;
        public const int SIG_F_SLURPY_PCL = 2048;

        public const uint FILTER_SALT = 0x9e3779b9;

        // records: $start-ip, $end-ip, $type, $goto, $lid
        public const int ON_NEXT = 1;
        public const int ON_LAST = 2;
        public const int ON_REDO = 3;
        public const int ON_RETURN = 4;
        public const int ON_DIE = 5;
        public const int ON_SUCCEED = 6;
        public const int ON_PROCEED = 7;
        public const int ON_GOTO = 8;
        public const int ON_NEXTDISPATCH = 9;
        public const int ON_VARLOOKUP = 10;
        public const int ON_WARNING = 11;
        // ON_VARLOOKUP is kinda special, it's not used for exceptions
        // but rather for $*FOO and the like; goto = the variable index
        public int[] edata;
        public string[] label_names;

        private static string[] controls = new string[] { "unknown", "next",
            "last", "redo", "return", "die", "succeed", "proceed", "goto",
            "nextsame/nextwith", "varlookup", "warning" };
        public static string DescribeControl(int type, Frame tgt,
                string name) {
            string ty = (type < controls.Length) ? controls[type] : "unknown";
            if (name != null) {
                return ty + "(" + name + (tgt != null ? ", lexotic)" : ", dynamic)");
            } else {
                return ty;
            }
        }

        public int GetInlineSlot(int ip, string name, int depth) {
            int end = 0;
            // ON_VARLOOKUP nodes are created in a set of 0 or more,
            // followed by a nameless one that marks the depth.
            // Find the correct end-node for the passed depth
            while (end < edata.Length) {
                if (edata[end+2] == ON_VARLOOKUP && edata[end+4] < 0 &&
                        edata[end+3] == depth && ip >= edata[end] &&
                        ip < edata[end+1])
                    break;
                end += 5;
            }
            if (end == edata.Length) return -1;
            while (true) {
                end -= 5;
                if (end < 0 || edata[end+2] != ON_VARLOOKUP ||
                        edata[end+4] < 0) // we've passed the end
                    return -1;
                if (name.Equals(label_names[edata[end+4]]))
                    return edata[end+3];
            }
        }

        public int FindControlEnt(int ip, int ty, string name) {
            for (int i = 0; i < edata.Length; i+=5) {
                if (ip < edata[i] || ip >= edata[i+1])
                    continue;
                if (ty != edata[i+2])
                    continue;
                if (name != null && (edata[i+4] < 0 || !name.Equals(label_names[edata[i+4]])))
                    continue;
                if (name == null && ty == ON_VARLOOKUP && edata[i+4] >= 0)
                    continue;
                return edata[i+3];
            }
            return -1;
        }

        internal List<SubInfo> GetPhasers(int t1) {
            int t2 = (t1 == Kernel.PHASER_KEEP) ? Kernel.PHASER_LEAVE : t1;
            List<SubInfo> r = new List<SubInfo>();
            foreach (SubInfo z in children) {
                if (z.phaser >= t1 && z.phaser <= t2) r.Add(z);
            }
            return r;
        }

        private string PName(int rbase) {
            return ((string)sig_r[rbase]) + " in " + name;
        }
        public unsafe Frame Binder(Frame caller, Frame outer, P6any sub,
                Variable[] pos, VarHash named, bool quiet, DispatchEnt de) {
            Frame th;
            if ((special & RUN_ONCE) != 0) {
                th = protopad;
                th.caller = caller;
                th.ip = 0;
                if (Frame.TraceCalls)
                    Console.WriteLine("{0}\t{1}", caller.info.name, name);
            } else {
                th = caller.MakeChild(outer, this, sub);
            }
            th.curDisp = de;
            th.pos = pos;
            th.named = named;
            // If we call an inferior runloop from the binder, we need for the
            // runloop to not overwrite th, which it would if the top frame
            // stayed 'caller'.
            Kernel.SetTopFrame(th);
            int[] ibuf = sig_i;
            if (ibuf == null) return th;
            int posc = 0;
            HashSet<string> namedc = null;
            int jun_pivot = -1;
            string jun_pivot_n = null;
            int jun_rank = int.MaxValue;
            if (named != null)
                namedc = new HashSet<string>(named.Keys);
            if (ibuf.Length == 0) goto noparams;
            fixed (int* ibase = ibuf) {
            int* ic = ibase;
            int* iend = ic + (ibuf.Length - 2);
            object[] rbuf = sig_r;
            int rc = 0;
            int obj_src = -1;
            string obj_src_n = null;

            while (ic < iend) {
                int flags = *(ic++);
                int slot  = *(ic++);
                int names = *(ic++);
                int rbase = rc;
                rc += (1 + names);
                if ((flags & SIG_F_HASDEFAULT) != 0) rc++;
                STable type = Kernel.AnyMO;
                obj_src = -1;
                if ((flags & SIG_F_HASTYPE) != 0)
                    type = (STable)rbuf[rc++];

                Variable src = null;
                if ((flags & SIG_F_SLURPY_PCL) != 0) {
                    src = Kernel.BoxAnyMO(pos, Kernel.ParcelMO);
                    posc  = pos.Length;
                    goto gotit;
                }
                if ((flags & SIG_F_SLURPY_CAP) != 0) {
                    P6any nw = new P6opaque(Kernel.CaptureMO);
                    nw.SetSlot("positionals", pos);
                    nw.SetSlot("named", named);
                    src = Kernel.NewROScalar(nw);
                    named = null; namedc = null; posc = pos.Length;
                    goto gotit;
                }
                if ((flags & SIG_F_SLURPY_POS) != 0) {
                    P6any l = new P6opaque(Kernel.ListMO);
                    Kernel.IterToList(l, Kernel.IterFlatten(
                                Kernel.SlurpyHelper(th, posc)));
                    src = Kernel.NewRWListVar(l);
                    posc = pos.Length;
                    goto gotit;
                }
                if ((flags & SIG_F_SLURPY_NAM) != 0) {
                    VarHash nh = new VarHash();
                    if (named != null) {
                        foreach (KeyValuePair<string,Variable> kv in named)
                            if (namedc.Contains(kv.Key))
                                nh[kv.Key] = kv.Value;
                        named = null;
                        namedc = null;
                    }
                    src = Kernel.BoxAnyMO(nh, Kernel.HashMO);
                    goto gotit;
                }
                if (names != 0 && named != null) {
                    for (int ni = 1; ni <= names; ni++) {
                        string n = (string)rbuf[rbase+ni];
                        if (namedc.Contains(n)) {
                            namedc.Remove(n);
                            src = named[n];
                            obj_src_n = n;
                            obj_src = -2;
                            goto gotit;
                        }
                    }
                }
                if ((flags & SIG_F_POSITIONAL) != 0 && posc != pos.Length) {
                    obj_src = posc;
                    src = pos[posc++];
                    goto gotit;
                }
get_default:
                if ((flags & SIG_F_HASDEFAULT) != 0) {
                    Frame thn = Kernel.GetInferiorRoot()
                        .MakeChild(th, (SubInfo) rbuf[rbase + 1 + names],
                            Kernel.AnyP);
                    src = Kernel.RunInferior(thn);
                    if (src == null)
                        throw new Exception("Improper null return from sub default for " + PName(rbase));
                    goto gotit;
                }
                if ((flags & SIG_F_DEFOUTER) != 0) {
                    Frame f = th;
                    if (outer_topic_key < 0) {
                        src = Kernel.AnyMO.typeVar;
                        goto gotit;
                    }
                    for (int i = 0; i < outer_topic_rank; i++) f = f.outer;
                    src = (Variable)f.GetDynamic(outer_topic_key);
                    goto gotit;
                }
                if ((flags & SIG_F_OPTIONAL) != 0) {
                    // Array is the "default" Positional -masak
                    if ((flags & SIG_F_IS_LIST) != 0)
                        src = Kernel.CreateArray();
                    else if ((flags & SIG_F_IS_HASH) != 0)
                        src = Kernel.CreateHash();
                    else
                        src = type.initVar;
                    goto gotit;
                }
                if (quiet) return null;
                return Kernel.Die(th, "No value for parameter " + PName(rbase));
gotit:
                if ((flags & SIG_F_RWTRANS) != 0) {
                } else if ((flags & SIG_F_IS_COPY) != 0) {
                    if ((flags & SIG_F_IS_HASH) != 0)
                        src = Kernel.Assign(Kernel.CreateHash(),
                            Kernel.NewRWListVar(src.Fetch()));
                    else if ((flags & SIG_F_IS_LIST) != 0)
                        src = Kernel.Assign(Kernel.CreateArray(),
                            Kernel.NewRWListVar(src.Fetch()));
                    else
                        src = Kernel.Assign(Kernel.NewTypedScalar(type), src);
                } else {
                    bool islist = ((flags & SIG_F_BINDLIST) != 0);
                    bool rw     = ((flags & SIG_F_READWRITE) != 0) && !islist;
                    P6any srco  = src.Fetch();

                    // XXX: in order for calling methods on Nil to work,
                    // self needs to be ignored here.
                    if (srco == Kernel.NilP && obj_src != -1 &&
                            (flags & SIG_F_INVOCANT) == 0) {
                        obj_src = -1;
                        goto get_default;
                    }
                    if (!srco.Does(type)) {
                        if (quiet) return null;
                        if (srco.mo.HasMRO(Kernel.JunctionMO) && obj_src != -1) {
                            int jrank = Kernel.UnboxAny<int>((P6any) ((P6opaque)srco).slots[0]) / 2;
                            if (jrank < jun_rank) {
                                jun_rank = jrank;
                                jun_pivot = obj_src;
                                jun_pivot_n = obj_src_n;
                            }
                            continue;
                        }
                        return Kernel.Die(th, "Nominal type check failed in binding " + PName(rbase) + "; got " + srco.mo.name + ", needed " + type.name);
                    }

                    if (rw) {
                        if (src.rw) {
                            // this will be a functional RW binding
                            if (src.whence != null)
                                Kernel.Vivify(src);
                            goto bound;
                        } else {
                            if (quiet) return null;
                            return Kernel.Die(th, "Binding " + PName(rbase) + ", cannot bind read-only value to is rw parameter");
                        }
                    }
                    else {
                        if (!src.rw && islist == src.islist)
                            goto bound;
                        src = new SimpleVariable(false, islist, srco.mo,
                                null, srco);
                    }
bound: ;
                }
                if ((flags & SIG_F_INVOCANT) != 0 && self_key >= 0)
                    th.SetDynamic(self_key, src);
                switch (slot + 1) {
                    case 0: break;
                    case 1:  th.lex0 = src; break;
                    case 2:  th.lex1 = src; break;
                    case 3:  th.lex2 = src; break;
                    case 4:  th.lex3 = src; break;
                    case 5:  th.lex4 = src; break;
                    case 6:  th.lex5 = src; break;
                    case 7:  th.lex6 = src; break;
                    case 8:  th.lex7 = src; break;
                    case 9:  th.lex8 = src; break;
                    case 10: th.lex9 = src; break;
                    default: th.lexn[slot - 10] = src; break;
                }
            }
            }
noparams:

            if (posc != pos.Length || namedc != null && namedc.Count != 0) {
                if (quiet) return null;
                string m = "Excess arguments to " + name;
                if (posc != pos.Length)
                    m += string.Format(", used {0} of {1} positionals",
                            posc, pos.Length);
                if (namedc != null && namedc.Count != 0)
                    m += ", unused named " + Kernel.JoinS(", ", namedc);
                return Kernel.Die(th, m);
            }

            if (jun_pivot != -1 && !quiet) {
                Variable jct = (jun_pivot == -2 ? named[jun_pivot_n] :
                        pos[jun_pivot]);
                th = caller.MakeChild(null, AutoThreadSubSI, Kernel.AnyP);

                P6opaque jo  = (P6opaque) jct.Fetch();

                th.named = named;
                th.pos = pos;
                th.lex1 = this;
                th.lex2 = jun_pivot_n;
                th.lex3 = jo.slots[0];
                th.lex4 = Kernel.UnboxAny<Variable[]>((P6any)jo.slots[1]);
                th.lex5 = outer;
                th.lex6 = sub;
                th.lex7 = de;
                th.lex8 = new Variable[((Variable[])th.lex4).Length];
                th.lex9 = jct;

                th.lexi0 = jun_pivot;
                th.lexi1 = 0;

                return th;
            }

            return th;
        }

        internal Variable GetOuterTopic(Frame f) {
            for (int i = 0; i < outer_topic_rank; i++) f = f.outer;
            return (Variable)f.GetDynamic(outer_topic_key);
        }

        static SubInfo AutoThreadSubSI = new SubInfo("KERNEL AutoThreadSub", AutoThreadSubC);
        static Frame AutoThreadSubC(Frame th) {
            Variable[] src = (Variable[]) th.lex4;
            Variable[] dst = (Variable[]) th.lex8;
            if (th.lexi1 > 0)
                dst[th.lexi1 - 1] = (Variable)th.resultSlot;

            if (th.lexi1 == dst.Length) {
                P6opaque nj = new P6opaque(Kernel.JunctionMO);
                nj.slots[0] = th.lex3;
                nj.slots[1] = Kernel.BoxRaw(dst, Kernel.ParcelMO);
                // restore, in case our caller is using this
                if (th.lexi0 == -2)
                    th.named[(string)th.lex2] = (Variable)th.lex9;
                else
                    th.pos[th.lexi0] = (Variable)th.lex9;
                th.caller.resultSlot = Kernel.NewROScalar(nj);
                return th.Return();
            }

            if (th.lexi0 == -2)
                th.named[(string)th.lex2] = src[th.lexi1++];
            else
                th.pos[th.lexi0] = src[th.lexi1++];

            return ((SubInfo)th.lex1).Binder(th, (Frame)th.lex5, (P6any)th.lex6,
                th.pos, th.named, false, (DispatchEnt)th.lex7);
        }

        internal bool IsInlinable() {
            if (sig_i == null)
                return false;
            if ((special & CANNOT_INLINE) != 0)
                return false;
            if (children.Count != 0)
                return false;
            foreach (KeyValuePair<string,LexInfo> kv in dylex) {
                if (!(kv.Value is LISimple))
                    return false;
                if ((kv.Key.Length > 0 &&
                            (kv.Key[0] == '?' || kv.Key[0] == '*')) ||
                        (kv.Key.Length > 1 &&
                          (kv.Key[1] == '?' || kv.Key[1] == '*')))
                    return false;
            }
            for (int i = 0; i < sig_i.Length; i += SIG_I_RECORD) {
                int fl = sig_i[i + SIG_I_FLAGS];
                if ((fl & SIG_F_POSITIONAL) == 0)
                    return false;
                if ((fl & SIG_F_HASDEFAULT) != 0)
                    return false;
            }
            return true;
        }

        internal void SetInlined() {
            special |= INLINED;
            outer.children.Remove(this);
            unit.our_subs.Remove(this);
            if (outervar != null && outer.dylex[outervar] is LISub)
                outer.dylex.Remove(outervar);
        }

        internal bool IsTopicalizer() {
            if (sig_i == null)
                return false;
            LexInfo topic;
            dylex.TryGetValue("$_", out topic);
            for (int i = 0; i < sig_i.Length; i += SIG_I_RECORD) {
                int slot = sig_i[i + SIG_I_SLOT];
                if (slot > 0 && topic != null && topic.SigIndex() == slot)
                    return true;
            }
            return false;
        }

        internal void AddLexical(string name, LexInfo li) {
            li.owner = this;
            li.name  = name;
            dylex[name] = li;
            dylex_filter |= FilterForName(name);
            li.BindFields();
            if (name == "self")
                self_key = li.SigIndex();
            if (protopad != null)
                li.Init(protopad);
        }

        internal void CreateProtopad() {
            if (protopad != null)
                return;
            if (outer != null)
                outer.CreateProtopad();

            // protopad is set when the parent's protopad is created,
            // or when we are
            protopad = new Frame(null, outer != null ? outer.protopad : null,
                this, protosub);

            foreach (SubInfo z in children)
                z.protosub = Kernel.MakeSub(z, protopad);
            foreach (LexInfo li in dylex.Values)
                li.Init(protopad);
        }

        public static uint FilterForName(string name) {
            if (name.Length < 2 || name[1] != '*') return 0;
            uint hash = (uint)(name.GetHashCode() * FILTER_SALT);
            return 1u << (int)(hash >> 27);
        }

        private SubInfo() { }
        public SubInfo(string name, int[] lines, DynBlockDelegate code,
                SubInfo outer, LAD ltm, int[] edata, string[] label_names,
                int nspill) {
            this.lines = lines;
            this.code = code;
            this.outer = outer;
            this.ltm = ltm;
            this.name = name;
            this.edata = edata;
            this.label_names = label_names;
            this.nspill = nspill;
            this.dylex = new Dictionary<string,LexInfo>();
            for (int i = 0; i < edata.Length; i += 5)
                if (edata[i+2] == ON_VARLOOKUP && edata[i+4] >= 0)
                    dylex_filter |= FilterForName(label_names[edata[i+4]]);
        }

        public SubInfo(RuntimeUnit unit, string name, SubInfo outer,
                STable cls, STable pkg, bool once) {
            edata = new int[0];
            this.name  = name;
            this.unit  = unit;
            this.mo    = cls;
            this.outer = outer;
            this.cur_pkg = pkg;

            SubInfo sc = outer;
            LexInfo li = null;
            for (outer_topic_rank = 1; sc != null; sc = sc.outer) {
                if (sc.dylex != null && sc.dylex.TryGetValue("$_", out li))
                    break;
                outer_topic_rank++;
            }
            outer_topic_key = (li is LIVarish) ? (li as LIVarish).index : -1;

            used_in_scope = new Dictionary<string,UsedInScopeInfo>();
            dylex = new Dictionary<string,LexInfo>();

            if (outer == null || outer.protopad != null)
                protosub = Kernel.MakeSub(this, outer == null ?
                        null : outer.protopad);
            if (once) {
                special |= RUN_ONCE;
                CreateProtopad();
            }
        }

        public SubInfo(string name, DynBlockDelegate code) :
            this(name, null, code, null, null, new int[0], null, 0) { }

        void IFreeze.Freeze(FreezeBuffer fb) {
            fb.Byte((byte)SerializationCode.SubInfo);
            // TODO - saving constant pools NYI
            string mn = null;
            string tn = null;
            if (code != null) {
                Type t = code.Method.DeclaringType;
                if (t.Assembly == typeof(Kernel).Assembly) {
                    tn = t.FullName;
                    mn = code.Method.Name;
                }
            }
            fb.String(mn);
            fb.String(tn);
            fb.Int(nspill);
            fb.Ints(sig_i);
            if (sig_i != null)
                fb.Refs(sig_r);

            fb.Ints(lines);
            fb.Int(dylex.Count);
            foreach (KeyValuePair<string, LexInfo> kv in dylex) {
                fb.String(kv.Key);
                kv.Value.DoFreeze(fb);
            }
            // not saving dylex_filter as it is a cache
            fb.String(name);
            fb.ObjRef(ltm);
            fb.Int(special);
            fb.Int(phaser);
            fb.String(outervar);
            fb.ObjRef(unit);
            fb.ObjRef(outer);
            fb.ObjRef(protosub);
            fb.ObjRef(protopad);
            fb.ObjRef(cur_pkg);
            fb.ObjRef(methodof);
            fb.ObjRef(body_of);
            fb.ObjRef(in_class);
            fb.ObjRef(mo);
            // not storing caches here
            // also not storing used_in_scope, it's compiler only data
            // TODO: we want to store the compiled code not this
            fb.String(nam_str);
            if (nam_str != null)
                fb.Refs(nam_refs);
            fb.Refs(param);
            fb.Refs(children);
            if (extend == null)
                fb.Int(0);
            else {
                fb.Int(extend.Count);
                foreach(KeyValuePair<string,object[]> kv in extend) {
                    fb.String(kv.Key);
                    fb.Refs(kv.Value);
                }
            }
        }

        internal static SubInfo Thaw(ThawBuffer tb) {
            SubInfo n = new SubInfo();
            tb.Register(n);
            // TODO - saving constant pools NYI
            string mn = tb.String();
            string tn = tb.String();
            if (tn != null) {
                n.code = (DynBlockDelegate) Delegate.CreateDelegate(
                    typeof(DynBlockDelegate),
                    typeof(Kernel).Assembly.GetType(tn, true).GetMethod(mn,
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static));
            }

            n.nspill = tb.Int();
            n.sig_i = tb.Ints();
            if (n.sig_i != null)
                n.sig_r = tb.RefsA<object>();

            n.lines = tb.Ints();
            int dyct = tb.Int();
            n.dylex = new Dictionary<string, LexInfo>();
            for (int i = 0; i < dyct; i++) {
                string key = tb.String();
                int type = tb.Byte();
                LexInfo li = null;
                switch (type) {
                    case (int) LexInfo.LexSerCode.Simple:
                        li = new LISimple(tb.Int(), tb.Byte(), (STable)tb.ObjRef());
                        break;
                    case (int) LexInfo.LexSerCode.Sub:
                        li = new LISub(tb.Int(), (SubInfo)tb.ObjRef());
                        break;
                    case (int) LexInfo.LexSerCode.Label:
                        li = new LILabel(tb.Int());
                        break;
                    case (int) LexInfo.LexSerCode.Dispatch:
                        li = new LIDispatch(tb.Int());
                        break;
                    case (int) LexInfo.LexSerCode.Common:
                        li = new LICommon(tb.String());
                        break;
                    case (int) LexInfo.LexSerCode.Hint:
                        li = new LIHint();
                        break;
                    case (int) LexInfo.LexSerCode.Package:
                        li = new LIPackage((STable) tb.ObjRef());
                        break;
                    case (int) LexInfo.LexSerCode.Alias:
                        li = new LIAlias(tb.String());
                        break;
                    default:
                        throw new ArgumentException(type.ToString());
                }
                n.dylex[key] = li;
                li.owner = n;
                li.name = key;
                n.dylex_filter |= FilterForName(key);
            }
            // not saving dylex_filter as it is a cache
            n.name = tb.String();
            n.ltm = (LAD) tb.ObjRef();
            n.special = tb.Int();
            n.phaser = tb.Int();
            n.outervar = tb.String();
            n.unit = (RuntimeUnit)tb.ObjRef();
            n.outer = (SubInfo)tb.ObjRef();
            n.protosub = (P6any)tb.ObjRef();
            n.protopad = (Frame)tb.ObjRef();
            n.cur_pkg = (STable)tb.ObjRef();
            n.methodof = (STable)tb.ObjRef();
            n.body_of = (STable)tb.ObjRef();
            n.in_class = (STable)tb.ObjRef();
            n.mo = (STable)tb.ObjRef();
            // not storing caches here
            // also not storing used_in_scope, it's compiler only data
            // TODO: we want to store the compiled code not this
            n.nam_str = tb.String();
            if (n.nam_str != null)
                n.nam_refs = tb.RefsA<object>();
            n.param = tb.RefsA<object>();
            n.children = tb.RefsL<SubInfo>();
            int nex = tb.Int();
            if (nex != 0) n.extend = new Dictionary<string,object[]>();
            for (int i = 0; i < nex; i++)
                n.extend[tb.String()] = tb.RefsA<object>();
            tb.PushFixup(n);
            return n;
        }

        void IFixup.Fixup() {
            SubInfo sc = outer;
            LexInfo li = null;
            for (outer_topic_rank = 1; sc != null; sc = sc.outer) {
                if (sc.dylex != null && sc.dylex.TryGetValue("$_", out li))
                    break;
                outer_topic_rank++;
            }
            outer_topic_key = (li is LIVarish) ? (li as LIVarish).index : -1;

            if (dylex.TryGetValue("self", out li)) {
                self_key = li.SigIndex();
            } else {
                self_key = -1;
            }

            foreach (SubInfo z in children) {
                if (z.phaser == Kernel.PHASER_CATCH)
                    catch_ = z;
                if (z.phaser == Kernel.PHASER_CONTROL)
                    control = z;
            }
        }
    }

    // This object fills the combined roles of Parrot LexPad and CallContext
    // objects.
    public class Frame: P6any, IFixup {
        // Used by trampoline to find destination
        public int ip = 0;
        public DynBlockDelegate code;
        // Storage for lexicals: most subs have a smallish number of lexicals,
        // and inlining them into the frame helps a lot.  Since Frame objects
        // are reused, bloating them doesn't hurt much
        public object lex0;
        public object lex1;
        public object lex2;
        public object lex3;
        public object lex4;
        public object lex5;
        public object lex6;
        public object lex7;
        public object lex8;
        public object lex9;

        // special slot that is filled by the callee before returning
        // used in some cases as a scratch slot by the codegen
        public object resultSlot = null;

        public int lexi0;
        public int lexi1;

        public object[] lexn; // Overflow for large subs

        // Link fields for other related frames, objects
        public Frame caller;
        public Frame outer;
        public SubInfo info; // the executing sub
        // a doubly-linked list of frames being used by a given coroutine
        public Frame reusable_child;
        public Frame reuser;
        // by being non-null marks the root frame of a coroutine, also
        // stores the Frame to return to when take is called if != this
        public Frame coro_return;

        // used by nextsame to find where to go
        public DispatchEnt curDisp;
        // stores extra data needed in regex frames
        public RxFrame rx;
        // for CALLER::<&?BLOCK>
        public P6any sub;
        // linked list of LEAVE phasers to call at return time
        public LeaveHook on_leave;

        // stores original capture for callframe.args
        public Variable[] pos;
        public VarHash named;

        // after MakeSub, GatherHelper
        public const int SHARED = 1;
        public int flags;

        public Frame(Frame caller_, Frame outer_,
                SubInfo info_, P6any sub_) {
            caller = caller_;
            outer = outer_;
            code = info_.code;
            info = info_;
            sub = sub_;
            mo = Kernel.CallFrameMO;
            lexn = (info_.nspill > 0) ? new object[info_.nspill] : null;
        }

        public Frame() { mo = Kernel.CallFrameMO; }

        public static readonly bool TraceCalls =
            Environment.GetEnvironmentVariable("NIECZA_TRACE_CALLS") != null;
        public static readonly bool VerboseExceptions =
            Environment.GetEnvironmentVariable("NIECZA_VERBOSE_EXCEPTIONS") != null;
        public static readonly bool AllExceptions =
            Environment.GetEnvironmentVariable("NIECZA_ALL_EXCEPTIONS") != null;

        public Frame MakeChild(Frame outer, SubInfo info, P6any sub) {
            if (reusable_child == null) {
                reusable_child = new Frame();
                reusable_child.reuser = this;
            }
            if (TraceCalls)
                Console.WriteLine("{0}\t{1}", this.info.name, info.name);
            reusable_child.ip = 0;
            reusable_child.resultSlot = null;
            reusable_child.lexn = (info.nspill != 0) ? new object[info.nspill] : null;
            reusable_child.on_leave = null;
            reusable_child.coro_return = null;
            reusable_child.lex0 = null;
            reusable_child.lex1 = null;
            reusable_child.lex2 = null;
            reusable_child.lex3 = null;
            reusable_child.lex4 = null;
            reusable_child.lex5 = null;
            reusable_child.lex6 = null;
            reusable_child.lex7 = null;
            reusable_child.lex8 = null;
            reusable_child.lex9 = null;
            reusable_child.curDisp = null;
            reusable_child.caller = this;
            reusable_child.outer = outer;
            reusable_child.info = info;
            reusable_child.sub = sub;
            reusable_child.code = info.code;
            reusable_child.rx = null;
            return reusable_child;
        }

        public Frame Continue() {
            return code(this);
        }

        public Variable ExtractNamed(string n) {
            Variable r;
            if (named != null && named.TryGetValue(n, out r)) {
                named.Remove(n);
                return r;
            } else {
                return null;
            }
        }

        public void MarkShared() {
            if (0 == (flags & SHARED)) {
                flags |= SHARED;
                if (reuser != null) reuser.reusable_child = reusable_child;
                if (reusable_child != null) reusable_child.reuser = reuser;
                reuser = reusable_child = null;
            }
        }

        // when control might re-enter a function
        public void MarkSharedChain() {
            for (Frame x = this; x != null; x = x.caller)
                x.MarkShared();
        }

        public int ExecutingLine() {
            if (info != null && info.lines != null) {
                return ip >= info.lines.Length ? 0 : info.lines[ip];
            } else {
                return 0;
            }
        }

        public Variable GetArgs() {
            P6any nw = new P6opaque(Kernel.CaptureMO);
            nw.SetSlot("positionals", pos ?? new Variable[0]);
            nw.SetSlot("named", named);
            return Kernel.NewROScalar(nw);
        }

        public string DescribeArgs() {
            string ret = null;
            try {
                Variable a = GetArgs();
                Variable sa = Kernel.RunInferior(a.Fetch().InvokeMethod(
                    Kernel.GetInferiorRoot(), "perl", new Variable[] { a },
                    null));
                ret = sa.Fetch().mo.mro_raw_Str.Get(sa);
            } catch (Exception ex) {
                ret = "[cannot display arguments: " + ex + "]";
            }
            return ret;
        }

        public string ExecutingFile() {
            if (info != null && info.unit != null && info.unit.filename != null)
                return info.unit.filename;
            else
                return "<unknown>";
        }

        public void SetDynamic(int ix, object v) {
            switch(ix) {
                case 0: lex0 = v; break;
                case 1: lex1 = v; break;
                case 2: lex2 = v; break;
                case 3: lex3 = v; break;
                case 4: lex4 = v; break;
                case 5: lex5 = v; break;
                case 6: lex6 = v; break;
                case 7: lex7 = v; break;
                case 8: lex8 = v; break;
                case 9: lex9 = v; break;
                default: lexn[ix-10] = v; break;
            }
        }

        public bool TryBindDynamic(string name, uint mask, object to) {
            if ((info.dylex_filter & mask) != mask)
                return false;
            int ix;
            LexInfo li;
            if ((ix = info.FindControlEnt(ip, SubInfo.ON_VARLOOKUP, name)) >= 0)
                SetDynamic(ix, to);
            else if (info.dylex.TryGetValue(name, out li))
                li.Set(this, to);
            else
                return false;

            return true;
        }

        public bool TryGetDynamic(string name, uint mask, out object v) {
            v = null;
            if ((info.dylex_filter & mask) != mask)
                return false;

            int ix;
            LexInfo li;

            if ((ix = info.FindControlEnt(ip, SubInfo.ON_VARLOOKUP, name))>=0)
                v = GetDynamic(ix);
            else if (info.dylex.TryGetValue(name, out li))
                v = li.Get(this);
            else
                return false;

            return true;
        }

        public object GetDynamic(int ix) {
            switch(ix) {
                case 0: return lex0;
                case 1: return lex1;
                case 2: return lex2;
                case 3: return lex3;
                case 4: return lex4;
                case 5: return lex5;
                case 6: return lex6;
                case 7: return lex7;
                case 8: return lex8;
                case 9: return lex9;
                default: return lexn[ix-10];
            }
        }

        public Variable LexicalFind(string name) {
            Frame csr = this;
            uint m = SubInfo.FilterForName(name);
            while (csr != null) {
                object o;
                if (csr.TryGetDynamic(name, m, out o)) {
                    return (Variable)o;
                }
                csr = csr.outer;
            }
            return Kernel.AnyMO.typeVar;
        }

        public void LexicalBind(string name, Variable to) {
            Frame csr = this;
            uint m = SubInfo.FilterForName(name);
            while (csr != null) {
                if (csr.TryBindDynamic(name, m, to))
                    return;
                csr = csr.outer;
            }
            if (name == "$!" || name == "$/") return;
            throw new NieczaException("cannot bind " + name + " in " + info.name);
        }

        public void PushLeave(int type, P6any thunk) {
            LeaveHook l = new LeaveHook();
            l.next = on_leave; on_leave = l;
            l.thunk = thunk; l.type = type;
        }

        public Frame DynamicCaller() {
            if (coro_return == null)
                return caller;
            return (Frame) coro_return;
        }

        private static List<string> spacey = new List<string>();
        public string DepthMark() {
            Frame f = this;
            int ix = 0;
            while (f != null) { ix++; f = f.caller; }
            while (spacey.Count <= ix) { spacey.Add(new String(' ', spacey.Count * 2)); }
            return spacey[ix];
        }

        public Frame Return() {
            if (on_leave != null) {
                Variable ret = (Variable) caller.resultSlot;
                bool ok = ret.Fetch().IsDefined();
                LeaveHook c = on_leave;
                // if exception is thrown, do not rerun LEAVEs
                on_leave = null;
                for (; c != null; c = c.next) {
                    if (0 == ((ok ? LeaveHook.KEEP : LeaveHook.UNDO) & c.type))
                        continue;
                    Variable r = Kernel.RunInferior(c.thunk.Invoke(
                        Kernel.GetInferiorRoot(), new Variable[] {ret}, null));
                    if ((c.type & LeaveHook.DIE) != 0 &&
                            !r.Fetch().mo.mro_raw_Bool.Get(r))
                        throw new NieczaException("Post-constraint failed for " + info.name);
                }
            }
            return caller;
        }
        public override void Freeze(FreezeBuffer fb) {
            fb.Byte((byte) SerializationCode.Frame);
            fb.Int(ip);
            // code will be reloaded from info.code
            fb.ObjRef(lex0);
            fb.ObjRef(lex1);
            fb.ObjRef(lex2);
            fb.ObjRef(lex3);
            fb.ObjRef(lex4);
            fb.ObjRef(lex5);
            fb.ObjRef(lex6);
            fb.ObjRef(lex7);
            fb.ObjRef(lex8);
            fb.ObjRef(lex9);
            fb.ObjRef(resultSlot); // probably not necessary
            fb.Int(lexi0);
            fb.Int(lexi1);
            fb.Refs(lexn);
            fb.ObjRef(caller); // XXX this might serialize too much
            fb.ObjRef(outer);
            fb.ObjRef(info);
            // we won't store reuse info :)
            fb.ObjRef(coro_return);
            fb.ObjRef(curDisp);
            fb.ObjRef(rx);
            fb.ObjRef(sub);
            for (LeaveHook l = on_leave; l != null; l = l.next) {
                fb.Byte(checked((byte)l.type));
                fb.ObjRef(l.thunk);
            }
            fb.Byte(LeaveHook.SENTINEL);
            fb.Refs(pos);
            fb.ObjRef(named);
            fb.Byte(checked((byte)flags));
        }
        internal static Frame Thaw(ThawBuffer tb) {
            Frame n = new Frame();
            tb.Register(n);
            n.ip = tb.Int();
            // code will be reloaded from info.code
            n.lex0 = tb.ObjRef();
            n.lex1 = tb.ObjRef();
            n.lex2 = tb.ObjRef();
            n.lex3 = tb.ObjRef();
            n.lex4 = tb.ObjRef();
            n.lex5 = tb.ObjRef();
            n.lex6 = tb.ObjRef();
            n.lex7 = tb.ObjRef();
            n.lex8 = tb.ObjRef();
            n.lex9 = tb.ObjRef();
            n.resultSlot = tb.ObjRef(); // probably not necessary
            n.lexi0 = tb.Int();
            n.lexi1 = tb.Int();
            n.lexn = tb.RefsA<object>();
            n.caller = (Frame)tb.ObjRef(); // XXX this might serialize too much
            n.outer = (Frame)tb.ObjRef();
            n.info = (SubInfo)tb.ObjRef();
            // we won't store reuse info :)
            n.coro_return = (Frame)tb.ObjRef();
            n.curDisp = (DispatchEnt)tb.ObjRef();
            n.rx = (RxFrame)tb.ObjRef();
            n.sub = (P6any)tb.ObjRef();

            LeaveHook fin = null;
            int type = tb.Byte();
            while (type != LeaveHook.SENTINEL) {
                if (fin == null) {
                    fin = n.on_leave = new LeaveHook();
                } else {
                    fin.next = new LeaveHook();
                    fin = fin.next;
                }
                fin.type = type;
                fin.thunk = (P6any) tb.ObjRef();
                type = tb.Byte();
            }

            n.pos = tb.RefsA<Variable>();
            n.named = (VarHash)tb.ObjRef();
            n.flags = tb.Byte();
            tb.PushFixup(n);
            return n;
        }
        void IFixup.Fixup() {
            code = info.code;
        }
    }

    public class NieczaException: Exception {
        // hide clr stack trace for these
        public override string ToString() { return Message; }
        public NieczaException(string detail) : base(detail) {}
        public NieczaException() : base() {}
    }

    public class ResumeUnwindException: Exception {
        public readonly int type, to_ip;
        public readonly Frame to_frame;
        public readonly object to_data;
        public readonly string p6backtrace;

        public override string ToString() { return p6backtrace; }

        public ResumeUnwindException(int type, Frame tf, int tip, object td,
                string bt) : base(bt) {
            p6backtrace = bt;
            to_data = td;
            to_ip = tip;
            to_frame = tf;
            this.type = type;
        }
    }

    class InvokeSub : InvokeHandler {
        public override Frame Invoke(P6any th, Frame caller,
                Variable[] pos, VarHash named) {
            if (!th.IsDefined())
                return Kernel.Die(caller, "Cannot invoke an undef sub");
            P6opaque dyo = ((P6opaque) th);
            Frame outer = (Frame) dyo.slots[0];
            SubInfo info = (SubInfo) dyo.slots[1];

            return info.Binder(caller, outer, th, pos, named, false, null);
        }
    }

    class InvokeCallMethod : InvokeHandler {
        public override Frame Invoke(P6any th, Frame caller,
                Variable[] pos, VarHash named) {
            Variable[] np = new Variable[pos.Length + 1];
            Array.Copy(pos, 0, np, 1, pos.Length);
            np[0] = Kernel.NewROScalar(th);
            return th.InvokeMethod(caller, "postcircumfix:<( )>", np, named);
        }
    }

    class PushyCallMethod : PushyHandler {
        string method;
        public PushyCallMethod(string method) { this.method = method; }
        public PushyCallMethod() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override Variable Invoke(Variable obj, Variable[] args) {
            Variable[] rargs = new Variable[args.Length + 1];
            Array.Copy(args, 0, rargs, 1, args.Length);
            rargs[0] = obj;
            return Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, rargs, null));
        }
    }

    class CtxCallMethodUnbox<T> : ContextHandler<T> {
        string method;
        public CtxCallMethodUnbox(string method) { this.method = method; }
        public CtxCallMethodUnbox() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override T Get(Variable obj) {
            Variable v = Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, new Variable[] { obj }, null));
            return Kernel.UnboxAny<T>(v.Fetch());
        }
    }
    class CtxCallMethodUnboxBool : ContextHandler<bool> {
        string method;
        public CtxCallMethodUnboxBool(string method) { this.method = method; }
        public CtxCallMethodUnboxBool() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override bool Get(Variable obj) {
            Variable v = Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, new Variable[] { obj }, null));
            return Kernel.UnboxAny<int>(v.Fetch()) != 0;
        }
    }

    class CtxCallMethodUnboxNumeric : ContextHandler<double> {
        string method;
        public CtxCallMethodUnboxNumeric(string method) { this.method = method; }
        public CtxCallMethodUnboxNumeric() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override double Get(Variable obj) {
            Variable v = method == null ? obj : Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, new Variable[] { obj }, null));
            P6any o = v.Fetch();
            if (o.mo.HasMRO(Kernel.NumMO)) {
                return Kernel.UnboxAny<double>(o);
            } else if (o.mo.HasMRO(Kernel.IntMO)) {
                if (o is BoxObject<int>) {
                    return (double)Kernel.UnboxAny<int>(o);
                } else {
                    return (double)Kernel.UnboxAny<BigInteger>(o);
                }
            } else if (o.mo.HasMRO(Kernel.RatMO)) {
                Rat r = Kernel.UnboxAny<Rat>(o);
                return (double)r.num / (double)r.den;
            } else if (o.mo.HasMRO(Kernel.FatRatMO)) {
                FatRat r = Kernel.UnboxAny<FatRat>(o);
                return (double)r.num / (double)r.den;
            } else if (o.mo.HasMRO(Kernel.ComplexMO)) {
                Complex r = Kernel.UnboxAny<Complex>(o);
                if (r.im != 0)
                    throw new NieczaException("coercion would discard nonzero imaginary part");
                return r.re;
            } else {
                throw new NieczaException("Numeric failed to return core numeric type");
            }
        }
    }

    class CtxCallMethod : ContextHandler<Variable> {
        string method;
        public CtxCallMethod(string method) { this.method = method; }
        public CtxCallMethod() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override Variable Get(Variable obj) {
            return Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, new Variable[] { obj }, null));
        }
    }

    class CtxCallMethodFetch : ContextHandler<P6any> {
        string method;
        public CtxCallMethodFetch(string method) { this.method = method; }
        public CtxCallMethodFetch() { }
        protected override object[] GetData() { return new object[]{method};}
        protected override void SetData(object[]o) { method = (string)o[0]; }

        public override P6any Get(Variable obj) {
            return Kernel.RunInferior(obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), method, new Variable[] { obj }, null)).Fetch();
        }
    }

    class CtxJustUnbox<T> : ContextHandler<T> {
        T dflt;
        public CtxJustUnbox(T dflt) { this.dflt = dflt; }
        public CtxJustUnbox() { }
        protected override object[] GetData() { return new object[]{dflt};}
        protected override void SetData(object[]o) { dflt = (T)o[0]; }
        public override T Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return dflt;
            return Kernel.UnboxAny<T>(o);
        }
    }

    class CtxBoolUnbox : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return false;
            return Kernel.UnboxAny<int>(o) != 0;
        }
    }

    class CtxReturnSelf : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            return Kernel.NewROScalar(obj.Fetch());
        }
    }

    class CtxReturnSelfList : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            if (obj.islist) return obj;
            return Kernel.NewRWListVar(obj.Fetch());
        }
    }

    class CtxReturnSelfItem : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            if (!obj.islist) return obj;
            return Kernel.NewROScalar(obj.Fetch());
        }
    }

    class CtxAnyList : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            VarDeque itr = new VarDeque(
                    obj.islist ? Kernel.NewROScalar(obj.Fetch()) : obj);
            P6any l = new P6opaque(Kernel.ListMO);
            Kernel.IterToList(l, itr);
            return Kernel.NewRWListVar(l);
        }
    }

    class IxParcelLISTSTORE : IndexHandler {
        public override Variable Get(Variable lhs, Variable rhs) {
            VarDeque src = Builtins.start_iter(rhs);
            P6any lhs_o = lhs.Fetch();
            if (!lhs_o.IsDefined())
                throw new NieczaException("assigning to undefined parcel");

            Variable[] dsts = Kernel.UnboxAny<Variable[]>(lhs_o);
            P6any[] srcs = new P6any[dsts.Length];

            for (int i = 0; i < dsts.Length; i++) {
                Variable d = dsts[i];
                if (d.islist) {
                    srcs[i] = new P6opaque(Kernel.ListMO);
                    Kernel.IterToList(srcs[i], src);
                    src = new VarDeque();
                } else {
                    srcs[i] = Kernel.IterHasFlat(src, true) ?
                        src.Shift().Fetch() : Kernel.AnyP;
                }
            }

            for (int i = 0; i < dsts.Length; i++) {
                Variable d = dsts[i];
                if (d.islist) {
                    d.Fetch().mo.mro_LISTSTORE.Get(d,
                            Kernel.NewRWListVar(srcs[i]));
                } else {
                    d.Store(srcs[i]);
                }
            }

            return lhs;
        }
    }
    class CtxParcelList : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            P6any o = obj.Fetch();
            VarDeque itr = o.IsDefined() ? new VarDeque(Kernel.UnboxAny<Variable[]>(o)) : new VarDeque();
            P6any l = new P6opaque(Kernel.ListMO);
            Kernel.IterToList(l, itr);
            return Kernel.NewRWListVar(l);
        }
    }

    class CtxBoxify<T> : ContextHandler<Variable> {
        ContextHandler<T> inner;
        STable box;
        public CtxBoxify() { }
        protected override object[] GetData() {
            return new object[] { inner, box };
        }
        protected override void SetData(object[] o) {
            inner = (ContextHandler<T>)o[0];
            box = (STable)o[1];
        }
        public CtxBoxify(ContextHandler<T> inner, STable box) {
            this.inner = inner;
            this.box = box;
        }
        public override Variable Get(Variable obj) {
            return Kernel.BoxAnyMO<T>(inner.Get(obj), box);
        }
    }

    class CtxBoxifyInty : ContextHandler<Variable> {
        ContextHandler<double> inner;
        public CtxBoxifyInty() { }
        protected override object[] GetData() {
            return new object[] { inner };
        }
        protected override void SetData(object[] o) {
            inner = (ContextHandler<double>)o[0];
        }
        public CtxBoxifyInty(ContextHandler<double> inner) {
            this.inner = inner;
        }
        public override Variable Get(Variable obj) {
            return Builtins.MakeInt((long)inner.Get(obj));
        }
    }

    class CtxContainerize : ContextHandler<Variable> {
        ContextHandler<P6any> inner;
        public CtxContainerize() { }
        protected override object[] GetData() {
            return new object[] { inner };
        }
        protected override void SetData(object[] o) {
            inner = (ContextHandler<P6any>)o[0];
        }
        public CtxContainerize(ContextHandler<P6any> inner) {
            this.inner = inner;
        }
        public override Variable Get(Variable obj) {
            return Kernel.NewROScalar(inner.Get(obj));
        }
    }

    class CtxParcelIterator : ContextHandler<VarDeque> {
        public override VarDeque Get(Variable obj) {
            P6any o = obj.Fetch();
            return o.IsDefined() ? new VarDeque(Kernel.UnboxAny<Variable[]>(o)) : new VarDeque();
        }
    }

    class IxArrayLISTSTORE : IndexHandler {
        public override Variable Get(Variable lhs, Variable rhs) {
            P6any lhs_o = lhs.Fetch();
            if (!lhs_o.IsDefined())
                throw new NieczaException("LISTSTORE to undefined Array");
            VarDeque iter = Builtins.start_iter(rhs);
            VarDeque items = new VarDeque();
            while (Kernel.IterHasFlat(iter, true))
                items.Push(Kernel.NewMuScalar(iter.Shift().Fetch()));
            lhs_o.SetSlot("items", items);
            lhs_o.SetSlot("rest", iter); /*now empty*/
            return lhs;
        }
    }
    class CtxListIterator : ContextHandler<VarDeque> {
        public override VarDeque Get(Variable obj) {
            P6opaque d = (P6opaque) obj.Fetch();
            if (!d.IsDefined()) return new VarDeque();
            VarDeque r = new VarDeque( (VarDeque) d.slots[0] );
            r.PushD((VarDeque) d.slots[1]);
            return r;
        }
    }

    class PopList : ContextHandler<Variable> {
        public override Variable Get(Variable v) {
            P6any o = v.Fetch();
            if (!o.IsDefined()) return Kernel.AnyMO.typeVar;
            VarDeque items = (VarDeque)o.GetSlot("items");
            VarDeque rest = (VarDeque)o.GetSlot("rest");
            while (Kernel.IterHasFlat(rest, false))
                items.Push(rest.Shift());
            return (items.Count() != 0) ? items.Pop() : Kernel.AnyMO.typeVar;
        }
    }
    class ShiftList : ContextHandler<Variable> {
        public override Variable Get(Variable v) {
            P6any o = v.Fetch();
            if (!o.IsDefined()) return Kernel.AnyMO.typeVar;
            VarDeque items = (VarDeque)o.GetSlot("items");
            VarDeque rest = (VarDeque)o.GetSlot("rest");
            if (items.Count() != 0)
                return items.Shift();
            if (Kernel.IterHasFlat(rest, false))
                return rest.Shift();
            return Kernel.AnyMO.typeVar;
        }
    }
    class UnshiftList : PushyHandler {
        public override Variable Invoke(Variable v, Variable[] args) {
            P6any o = v.Fetch();
            if (!o.IsDefined())
                throw new NieczaException("Cannot push onto type object");
            VarDeque iter = new VarDeque(args);
            VarDeque targ = (VarDeque)o.GetSlot("items");
            VarDeque st = new VarDeque();
            while (Kernel.IterHasFlat(iter, true))
                st.Push(Kernel.NewMuScalar(iter.Shift().Fetch()));
            targ.UnshiftD(st);
            return v;
        }
    }
    class PushList : PushyHandler {
        public override Variable Invoke(Variable v, Variable[] args) {
            P6any o = v.Fetch();
            if (!o.IsDefined())
                throw new NieczaException("Cannot push onto type object");
            VarDeque iter = new VarDeque(args);
            VarDeque targ = (VarDeque)o.GetSlot("rest");
            if (targ.Count() == 0) targ = (VarDeque)o.GetSlot("items");
            while (Kernel.IterHasFlat(iter, true))
                targ.Push(Kernel.NewMuScalar(iter.Shift().Fetch()));
            return v;
        }
    }

    class IxHashLISTSTORE : IndexHandler {
        public override Variable Get(Variable lhs, Variable rhs) {
            P6any lhs_o = lhs.Fetch();
            if (!lhs_o.IsDefined())
                throw new NieczaException("LISTSTORE to undefined Hash");
            VarHash into = new VarHash();
            VarDeque iter = Builtins.start_iter(rhs);
            bool first = true;
            while (Kernel.IterHasFlat(iter, true)) {
                P6any elt = iter.Shift().Fetch();
                if (first && elt.mo.HasMRO(Kernel.HashMO)) {
                    foreach(KeyValuePair<string,Variable> kv in
                            Kernel.UnboxAny<VarHash>(elt)) {
                        into[kv.Key] = kv.Value;
                    }
                } else if (elt.mo.HasMRO(Kernel.PairMO)) {
                    Variable k = (Variable) elt.GetSlot("key");
                    Variable v = (Variable) elt.GetSlot("value");
                    into[k.Fetch().mo.mro_raw_Str.Get(k)] =
                        Kernel.NewMuScalar(v.Fetch());
                } else {
                    if (!Kernel.IterHasFlat(iter, true))
                        throw new NieczaException("Unmatched key in Hash.LISTSTORE");
                    into[elt.mo.mro_raw_Str.Get(Kernel.NewROScalar(elt))] =
                        Kernel.NewMuScalar(iter.Shift().Fetch());
                }
                first = false;
            }
            Kernel.SetBox<VarHash>(lhs_o, into);
            return lhs;
        }
    }
    class CtxHashIterator : ContextHandler<VarDeque> {
        public override VarDeque Get(Variable obj) {
            return Builtins.HashIterRaw(3, obj);
        }
    }
    class CtxHashBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            return o.IsDefined() && Kernel.UnboxAny<VarHash>(o).IsNonEmpty;
        }
    }

    class CtxRawNativeDefined : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            return obj.Fetch().IsDefined();
        }
    }

    class CtxBoolNativeDefined : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            return obj.Fetch().IsDefined() ? Kernel.TrueV : Kernel.FalseV;
        }
    }

    class CtxNumSuccish : ContextHandler<P6any> {
        double amt;
        public CtxNumSuccish() { }
        protected override object[] GetData() { return new object[] { amt }; }
        protected override void SetData(object[] o) { amt = (double) o[0]; }
        public CtxNumSuccish(double amt) { this.amt = amt; }
        public override P6any Get(Variable obj) {
            P6any o = obj.Fetch();
            double v = o.IsDefined() ? Kernel.UnboxAny<double>(o) : 0;
            return Kernel.BoxRaw(v + amt, Kernel.NumMO);
        }
    }
    class CtxRawNativeNum2Str : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            return o.IsDefined() ? Utils.N2S(Kernel.UnboxAny<double>(o)) : "Num()";
        }
    }
    class CtxNum2Bool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            return o.IsDefined() && Kernel.UnboxAny<double>(o) != 0;
        }
    }

    class CtxIntSuccish : ContextHandler<P6any> {
        int amt;
        public CtxIntSuccish() { }
        protected override object[] GetData() { return new object[] { amt }; }
        protected override void SetData(object[] o) { amt = (int) o[0]; }
        public CtxIntSuccish(int amt) { this.amt = amt; }
        public override P6any Get(Variable obj) {
            P6any o = obj.Fetch();
            int v;
            if (o is BoxObject<BigInteger>) {
                BigInteger bn = Kernel.UnboxAny<BigInteger>(o) + amt;
                if (bn.AsInt32(out v))
                    return Kernel.BoxRaw<int>(v, Kernel.IntMO);
                else
                    return Kernel.BoxRaw<BigInteger>(bn, Kernel.IntMO);
            }
            v = o.IsDefined() ? Kernel.UnboxAny<int>(o) : 0;
            if (v == (amt > 0 ? int.MaxValue : int.MinValue))
                return Kernel.BoxRaw<BigInteger>(amt + (long)v, Kernel.IntMO);
            return Kernel.BoxRaw(v + amt, Kernel.IntMO);
        }
    }
    class CtxIntStr : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return o.mo.name + "()"; }
            else if (o is BoxObject<int>) { return Utils.N2S(Kernel.UnboxAny<int>(o)); }
            else { return Kernel.UnboxAny<BigInteger>(o).ToString(); }
        }
    }
    class CtxIntBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return false; }
            else if (o is BoxObject<int>) { return Kernel.UnboxAny<int>(o)!=0; }
            else { return Kernel.UnboxAny<BigInteger>(o) != BigInteger.Zero; }
        }
    }

    class CtxRatSuccish : ContextHandler<P6any> {
        bool up;
        public CtxRatSuccish() { }
        protected override object[] GetData() { return new object[] { up }; }
        protected override void SetData(object[] o) { up = (bool) o[0]; }
        public CtxRatSuccish(bool up) { this.up = up; }
        public override P6any Get(Variable obj) {
            P6any o = obj.Fetch();
            Rat rr;
            if (o.IsDefined()) {
                Rat ir = Kernel.UnboxAny<Rat>(o);
                rr = new Rat(up ? ir.num + ir.den : ir.num - ir.den, ir.den);
            } else {
                rr = new Rat(up ? BigInteger.One : BigInteger.MinusOne, 1);
            }
            return Kernel.BoxRaw<Rat>(rr, Kernel.RatMO);
        }
    }
    class CtxRatStr : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return o.mo.name + "()"; }
            Rat r = Kernel.UnboxAny<Rat>(o);
            return r.num.ToString() + "/" + r.den.ToString();
        }
    }
    class CtxRatBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return false; }
            Rat r = Kernel.UnboxAny<Rat>(o);
            return r.num != BigInteger.Zero;
        }
    }

    class CtxFatRatSuccish : ContextHandler<P6any> {
        bool up;
        public CtxFatRatSuccish(bool up) { this.up = up; }
        public CtxFatRatSuccish() { }
        protected override object[] GetData() { return new object[] { up }; }
        protected override void SetData(object[] o) { up = (bool) o[0]; }
        public override P6any Get(Variable obj) {
            P6any o = obj.Fetch();
            FatRat rr;
            if (o.IsDefined()) {
                FatRat ir = Kernel.UnboxAny<FatRat>(o);
                rr = new FatRat(up ? ir.num + ir.den : ir.num - ir.den, ir.den);
            } else {
                rr = new FatRat(up ? BigInteger.One : BigInteger.MinusOne, BigInteger.One);
            }
            return Kernel.BoxRaw<FatRat>(rr, Kernel.FatRatMO);
        }
    }
    class CtxFatRatStr : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return o.mo.name + "()"; }
            FatRat r = Kernel.UnboxAny<FatRat>(o);
            return r.num.ToString() + "/" + r.den.ToString();
        }
    }
    class CtxFatRatBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return false; }
            FatRat r = Kernel.UnboxAny<FatRat>(o);
            return r.num != BigInteger.Zero;
        }
    }

    class CtxComplexSuccish : ContextHandler<P6any> {
        double amt;
        public CtxComplexSuccish(double amt) { this.amt = amt; }
        public CtxComplexSuccish() { }
        protected override object[] GetData() { return new object[] { amt }; }
        protected override void SetData(object[] o) { amt = (double) o[0]; }
        public override P6any Get(Variable obj) {
            P6any o = obj.Fetch();
            Complex c = o.IsDefined() ? Kernel.UnboxAny<Complex>(o) : null;
            c = (c == null) ? new Complex(amt, 0) : new Complex(c.re+amt, c.im);
            return Kernel.BoxRaw(c, Kernel.ComplexMO);
        }
    }
    class CtxComplexStr : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return o.mo.name + "()"; }
            Complex r = Kernel.UnboxAny<Complex>(o);
            return Utils.N2S(r.re) + (r.im < 0 ? "" : "+") + Utils.N2S(r.im) + "i";
        }
    }
    class CtxComplexBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) { return false; }
            Complex r = Kernel.UnboxAny<Complex>(o);
            return r.re != 0 || r.im != 0;
        }
    }

    class CtxStrBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return false;
            string s = Kernel.UnboxAny<string>(o);
            return !(s == "" || s == "0");
        }
    }
    class CtxStrSuccish : ContextHandler<P6any> {
        bool succ;
        public CtxStrSuccish(bool succ) { this.succ = succ; }
        public CtxStrSuccish() { }
        protected override object[] GetData() { return new object[] { succ }; }
        protected override void SetData(object[] o) { succ = (bool) o[0]; }
        // note that most of this table is katakana.  Perhaps there
        // is a better way.
        static ushort[] table = {
            48, 57, 57, 48, 65, 90, 90, 65, 97, 122, 122, 97, 913, 929,
            937, 931, 931, 937, 929, 913, 945, 961, 969, 963, 963, 969,
            961, 945, 8544, 8555, 8555, 8544, 8560, 8571, 8571, 8560,
            9312, 9331, 9331, 9312, 9332, 9351, 9351, 9332, 9372, 9397,
            9397, 9372, 9856, 9861, 9861, 9856, 12450, 12450, 12531,
            12452, 12452, 12452, 12450, 12454, 12454, 12454, 12452,
            12456, 12456, 12456, 12454, 12458, 12458, 12459, 12456,
            12461, 12461, 12461, 12459, 12463, 12463, 12463, 12461,
            12465, 12465, 12465, 12463, 12467, 12467, 12467, 12465,
            12469, 12469, 12469, 12467, 12471, 12471, 12471, 12469,
            12473, 12473, 12473, 12471, 12475, 12475, 12475, 12473,
            12477, 12477, 12477, 12475, 12479, 12479, 12479, 12477,
            12481, 12481, 12481, 12479, 12484, 12484, 12484, 12481,
            12486, 12486, 12486, 12484, 12488, 12488, 12488, 12486,
            12490, 12490, 12495, 12488, 12498, 12498, 12498, 12495,
            12501, 12501, 12501, 12498, 12504, 12504, 12504, 12501,
            12507, 12507, 12507, 12504, 12510, 12510, 12514, 12507,
            12516, 12516, 12516, 12514, 12518, 12518, 12518, 12516,
            12520, 12520, 12525, 12518, 12527, 12527, 12527, 12525,
            12530, 12530, 12531, 12527, 12450
        };
        // would perfect hashing be better?
        void TableGet(char it, out char prev, out char next) {
            int al = 0;
            int ah = table.Length / 4;
            while (true) {
                if (al >= ah) {
                    prev = next = it;
                    return;
                }
                int am = (al + ah) / 2;
                if (it < (char)table[am*4]) {
                    ah = am;
                } else if (it <= (char)table[am*4+1]) {
                    prev = (it == (char)table[am*4]) ? (char)table[am*4+2] : (char)(it-1);
                    next = (it == (char)table[am*4+1]) ? (char)table[am*4+3] : (char)(it+1);
                    return;
                } else {
                    al = am+1;
                }
            }
        }

        bool Digitish(char it) {
            char next, prev;
            TableGet(it, out prev, out next);
            return (next != it);
        }

        public override P6any Get(Variable obj) {
            P6any obj_o = obj.Fetch();
            if (!obj_o.IsDefined()) return Kernel.BoxRaw("WTF", Kernel.StrMO);
            string src = Kernel.UnboxAny<string>(obj_o);
            int right = src.Length;
tryagain:
            while (right != 0 && !Digitish(src[right-1])) right--;
            if (right == 0) return Kernel.BoxRaw("WTF", Kernel.StrMO);
            int left = right;
            while (left != 0 && Digitish(src[left-1])) left--;
            if (left != 0 && src[left-1] == '.') {
                right--;
                goto tryagain;
            }
            char[] nbuf = new char[src.Length + 1];
            for (int i = right; i < src.Length; i++) nbuf[i+1] = src[i];

            int delta = 0;
            if (succ) {
                bool carry = true;
                char zero = '\0';
                while (carry && right != left) {
                    char next, prev;
                    TableGet(src[right-1], out prev, out next);
                    carry = (next < src[right-1]);
                    zero = next;
                    nbuf[right] = next;
                    right--;
                }
                if (carry) {
                    delta++;
                    nbuf[left] = zero == '0' ? '1' : zero;
                }
            } else {
                bool borrow = true;
                while (borrow && right != left) {
                    char next, prev;
                    TableGet(src[right-1], out prev, out next);
                    borrow = (src[right-1] < prev);
                    nbuf[right] = prev;
                    right--;
                }
                if (borrow)
                    throw new NieczaException("Magical string decrement underflowed");
            }
            for (int i = 0; i < right; i++) nbuf[i+1-delta] = src[i];
            return Kernel.BoxRaw(new string(nbuf, 1-delta, src.Length+delta),
                    Kernel.StrMO);
        }
    }

    class CtxListBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return false;
            P6opaque dob = (P6opaque) o;
            VarDeque items = (VarDeque) dob.slots[0];
            if (items.Count() != 0) return true;
            VarDeque rest = (VarDeque) dob.slots[1];
            if (rest.Count() == 0) return false;
            if (Kernel.IterHasFlat(rest, false)) {
                items.Push(rest.Shift());
                return true;
            } else {
                return false;
            }
        }
    }

    class CtxListNum : ContextHandler<double> {
        public override double Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return 0;
            P6opaque dob = (P6opaque) o;
            VarDeque items = (VarDeque) dob.slots[0];
            VarDeque rest = (VarDeque) dob.slots[1];
            if (rest.Count() == 0) return items.Count();
            while (Kernel.IterHasFlat(rest, false)) {
                items.Push(rest.Shift());
            }
            return items.Count();
        }
    }

    class CtxJunctionBool : ContextHandler<bool> {
        public override bool Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return false;
            P6opaque o_ = (P6opaque)o;

            int jtype = Kernel.UnboxAny<int>((P6any) o_.slots[0]);
            if (jtype == 4) return true; // XXX

            Variable[] eigen = Kernel.UnboxAny<Variable[]>((P6any) o_.slots[1]);
            int ix = 0;
            Variable v;
            // logic design taken from Rakudo here
            switch(jtype) {
                case 0: //all
                    if (ix == eigen.Length) return true;
                    v = eigen[ix++];
                    if (!v.Fetch().mo.mro_raw_Bool.Get(v)) return false;
                    goto case 0;
                case 1: //none
                    if (ix == eigen.Length) return true;
                    v = eigen[ix++];
                    if (v.Fetch().mo.mro_raw_Bool.Get(v)) return false;
                    goto case 1;
                case 2: //one, searching for first
                    if (ix == eigen.Length) return false;
                    v = eigen[ix++];
                    if (v.Fetch().mo.mro_raw_Bool.Get(v)) goto case 1;
                    goto case 2;
                case 3: //any
                    if (ix == eigen.Length) return false;
                    v = eigen[ix++];
                    if (v.Fetch().mo.mro_raw_Bool.Get(v)) return true;
                    goto case 3;
                default: throw new ArgumentException();
            }
        }
    }

    class CtxMatchStr : ContextHandler<string> {
        public override string Get(Variable obj) {
            P6any o = obj.Fetch();
            if (!o.IsDefined()) return "";
            Cursor c = (Cursor) o;
            return c.GetBacking().Substring(c.from, c.pos - c.from);
        }
    }

    class CtxStrNativeNum2Str : ContextHandler<Variable> {
        public override Variable Get(Variable obj) {
            P6any o = obj.Fetch();
            return Kernel.BoxAnyMO<string>(o.IsDefined() ? Utils.N2S(Kernel.UnboxAny<double>(o)) : "Num()", Kernel.StrMO);
        }
    }

    class IxCallMethod : IndexHandler {
        string name;
        VarHash named;
        public IxCallMethod() { }
        protected override object[] GetData() {
            return new object[] { name, named };
        }
        protected override void SetData(object[] o) {
            name = (string) o[0];
            named = (VarHash) o[1];
        }
        public IxCallMethod(string name, string adv) {
            this.name = name;
            if (adv != null) {
                named = new VarHash();
                named[adv] = Kernel.TrueV;
            }
        }
        public override Variable Get(Variable obj, Variable key) {
            return (Variable) Kernel.RunInferior(
                    obj.Fetch().InvokeMethod(Kernel.GetInferiorRoot(), name,
                        new Variable[] { obj, key }, named));
        }
    }

    class KeySlicer : IndexHandler {
        int mode; IndexHandler bas;
        public KeySlicer() { }
        protected override object[] GetData() {
            return new object[] { mode, bas };
        }
        protected override void SetData(object[] o) {
            mode = (int) o[0];
            bas  = (IndexHandler) o[1];
        }
        public KeySlicer(int mode, object bas) {
            this.mode = mode; this.bas = (IndexHandler)bas;
        }

        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            switch (mode) {
                case 0:  return key;
                case 1:  return Builtins.MakeParcel(key, bas.Get(obj, key));
                default: return Builtins.pair(key, bas.Get(obj, key));
            }
        }
    }

    class IxAnyDeleteKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return Kernel.AnyMO.typeVar;
            return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                "delete_key", new Variable[] { obj, key }, null));
        }
    }
    class IxAnyExistsKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return Kernel.FalseV;
            return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                "exists_key", new Variable[] { obj, key }, null));
        }
    }
    class IxAnyBindKey : BindHandler {
        public override Variable Bind(Variable obj, Variable key, Variable to) {
            P6any os = obj.Fetch();
            if (os.IsDefined())
                return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                    "bind_key", new Variable[] { obj, key, to }, null));
            obj.Store(Kernel.BoxRaw(new VarHash(), Kernel.HashMO));
            return Kernel.HashMO.mro_bind_key.Bind(obj, key, to);
        }
    }
    class IxAnyBindPos : BindHandler {
        public override Variable Bind(Variable obj, Variable key, Variable to) {
            P6any os = obj.Fetch();
            if (os.IsDefined())
                return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                    "bind_pos", new Variable[] { obj, key, to }, null));
            obj.Store(Kernel.CreateArray().Fetch());
            return Kernel.ArrayMO.mro_bind_key.Bind(obj, key, to);
        }
    }
    class IxAnyAtKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return IndexHandler.ViviHash(obj, key);
            return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                "at_key", new Variable[] { obj, key }, null));
        }
    }
    class IxAnyAtPos : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return IndexHandler.ViviArray(obj, key);
            if (ks.mo != Kernel.IntMO && ks.mo.HasMRO(Kernel.CodeMO)) {
                Variable elts = Kernel.RunInferior(os.InvokeMethod(
                        Kernel.GetInferiorRoot(), "elems",
                        new Variable[] { obj }, null));
                return Get(obj, Kernel.RunInferior(ks.Invoke(
                    Kernel.GetInferiorRoot(),
                    new Variable[] { elts }, null)));
            }

            return Kernel.RunInferior(os.InvokeMethod(Kernel.GetInferiorRoot(),
                "at_pos", new Variable[] { obj, key }, null));
        }
    }

    class IxCursorAtKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);
            P6any o = obj.Fetch();
            if (!o.IsDefined())
                return Kernel.AnyMO.typeVar;

            Cursor os = (Cursor)o;
            return os.GetKey(ks.mo.mro_raw_Str.Get(key));
        }
    }
    class IxCursorAtPos : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any o = obj.Fetch();
            if (!o.IsDefined())
                return Kernel.AnyMO.typeVar;

            Cursor os = (Cursor)o;
            return os.GetKey(Utils.N2S(ks.mo.mro_raw_Numeric.Get(key)));
        }
    }

    class IxHashBindKey : BindHandler {
        public override Variable Bind(Variable obj, Variable key, Variable to) {
            P6any ks = key.Fetch();
            P6any os = obj.Fetch();
            if (!os.IsDefined())
                obj.Store(os = Kernel.BoxRaw(new VarHash(), Kernel.HashMO));
            string kss = ks.mo.mro_raw_Str.Get(key);
            VarHash h = Kernel.UnboxAny<VarHash>(os);

            return h[kss] = Kernel.NewBoundVar(Kernel.NBV_RW, Kernel.MuMO, to);
        }
    }
    class IxHashAtKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return IndexHandler.ViviHash(obj, key);
            string kss = ks.mo.mro_raw_Str.Get(key);
            VarHash h = Kernel.UnboxAny<VarHash>(os);
            Variable r;
            if (h.TryGetValue(kss, out r))
                return r;
            return new SimpleVariable(true, false, Kernel.MuMO,
                    new HashViviHook(os, kss), Kernel.AnyP);
        }
    }
    class IxHashExistsKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);
            P6any os = obj.Fetch();
            if (!os.IsDefined()) return Kernel.FalseV;
            string kss = ks.mo.mro_raw_Str.Get(key);
            VarHash h = Kernel.UnboxAny<VarHash>(os);
            return h.ContainsKey(kss) ? Kernel.TrueV : Kernel.FalseV;
        }
    }
    class IxHashDeleteKey : IndexHandler {
        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);
            P6any os = obj.Fetch();
            if (!os.IsDefined()) return Kernel.AnyMO.typeVar;
            string kss = ks.mo.mro_raw_Str.Get(key);
            VarHash h = Kernel.UnboxAny<VarHash>(os);
            Variable r;
            if (h.TryGetValue(kss, out r)) {
                h.Remove(kss);
                return r;
            } else {
                return Kernel.AnyMO.typeVar;
            }
        }
    }

    class IxListAtPos : IndexHandler {
        bool extend;
        public IxListAtPos(bool extend) { this.extend = extend; }
        public IxListAtPos() { }
        protected override object[] GetData() { return new object[] { extend};}
        protected override void SetData(object[] o) { extend = (bool)o[0]; }

        public override Variable Get(Variable obj, Variable key) {
            P6any ks = key.Fetch();
            if (key.islist || !ks.mo.is_any && ks.mo.HasMRO(Kernel.JunctionMO))
                return Slice(obj, key);

            P6any os = obj.Fetch();
            if (!os.IsDefined())
                return IndexHandler.ViviArray(obj, key);

            P6opaque dos = (P6opaque) os;
            VarDeque items = (VarDeque) dos.slots[0];
            VarDeque rest  = (VarDeque) dos.slots[1];

            if (ks.mo != Kernel.IntMO && ks.mo.HasMRO(Kernel.CodeMO)) {
                Variable nr = os.mo.mro_Numeric.Get(obj);
                return Get(obj, Kernel.RunInferior(ks.Invoke(
                    Kernel.GetInferiorRoot(),
                    new Variable[] { nr }, null)));
            }

            int ix = (int) key.Fetch().mo.mro_raw_Numeric.Get(key);
            while (items.Count() <= ix && Kernel.IterHasFlat(rest, false)) {
                items.Push(rest.Shift());
            }
            if (ix < 0)
                return Kernel.AnyMO.typeVar;
            if (items.Count() <= ix) {
                if (extend) {
                    return new SimpleVariable(true, false, Kernel.MuMO,
                            new ArrayViviHook(os, ix), Kernel.AnyP);
                } else {
                    return Kernel.AnyMO.typeVar;
                }
            }
            return items[ix];
        }
    }

    class IxListBindPos : BindHandler {
        public override Variable Bind(Variable obj, Variable key, Variable to) {
            P6any ks = key.Fetch();
            P6any os = obj.Fetch();
            if (!os.IsDefined())
                obj.Store(os = Kernel.CreateArray().Fetch());

            P6opaque dos = (P6opaque) os;
            VarDeque items = (VarDeque) dos.slots[0];
            VarDeque rest  = (VarDeque) dos.slots[1];

            if (ks.mo != Kernel.IntMO && ks.mo.HasMRO(Kernel.CodeMO)) {
                Variable nr = os.mo.mro_Numeric.Get(obj);
                key = Kernel.RunInferior(ks.Invoke(Kernel.GetInferiorRoot(),
                    new Variable[] { nr }, null));
            }

            int ix = (int) key.Fetch().mo.mro_raw_Numeric.Get(key);
            while (items.Count() <= ix && Kernel.IterHasFlat(rest, false)) {
                items.Push(rest.Shift());
            }
            if (ix < 0)
                throw new NieczaException("binding to out of range slot " + ix);
            while (items.Count() <= ix) {
                items.Push(Kernel.NewTypedScalar(null));
            }
            return items[ix] = Kernel.NewBoundVar(Kernel.NBV_RW, Kernel.MuMO, to);
        }
    }

    public struct StashCursor {
        public const int WHO  = 0; // use p1(P6any)
        public const int LEX  = 1; // p1(Frame) p2(int, depth)
        public const int ROOT = 2; // p1(Frame) p2(int)
        public const int DYNA = 3; // p1&p2
        public const int CLR  = 4; // p1(string)

        int type;
        object p1;
        int p2;

        public StashCursor(Frame fr, int depth) {
            this.type = ROOT;
            this.p1 = fr;
            this.p2 = depth;
        }

        public static Variable MakePackage(string name, P6any who) {
            STable st = new STable(name);
            st.who = who;
            st.typeObject = st.initObject = new P6opaque(st, 0);
            ((P6opaque)st.typeObject).slots = null;
            st.typeVar = st.initVar = Kernel.NewROScalar(st.typeObject);
            st.mo.isPackage = true;
            st.mo.rtype = "package";
            // XXX should be PackageHOW
            st.how = new BoxObject<STable>(st, Kernel.ClassHOWMO, 0);
            st.mo.Revalidate();
            return st.typeVar;
        }

        bool HasCaller() {
            Frame f = (Frame)p1;
            if (p2 == 0) {
                f = f.caller;
                if (f != null && f.info == Kernel.ExitRunloopSI) f = f.caller;
                if (f == null) return false;
            }
            return true;
        }

        StashCursor ToCaller() {
            StashCursor r = this;
            Frame f = (Frame)p1;
            if (r.p2 > 0) r.p2--;
            else {
                f = f.caller;
                if (f != null && f.info == Kernel.ExitRunloopSI) f = f.caller;
                if (f == null)
                    throw new NieczaException("No more calling frames");
                r.p1 = f;
                r.p2 = f.info.FindControlEnt(f.ip, SubInfo.ON_VARLOOKUP, null);
                if (r.p2 < 0) r.p2 = 0;
            }
            return r;
        }

        SubInfo ToInfo() { return (p1 as Frame).info; }

        StashCursor ToOuter() {
            StashCursor r = this;
            if (r.p2 > 0) { r.p2--; }
            else {
                Frame f = ((Frame) r.p1).outer;
                r.p1 = f;
                if (f == null)
                    throw new NieczaException("No more outer frames");
                r.p2 = 0;
            }
            return r;
        }

        bool TryLexOut(string key, bool rbar_w, ref Variable o) {
            StashCursor sc = this;
            if (key.Length >= 2 && key[1] == '*') {
                return TryDynamic(key, rbar_w, ref o);
            }
            while (true) {
                if (sc.TryLex(key, rbar_w, ref o)) return true;
                if ((sc.p1 as Frame).outer == null && sc.p2 == 0)
                    return false;
                sc = sc.ToOuter();
            }
        }

        bool TryDynamic(string key, bool rbar_w, ref Variable o) {
            StashCursor sc = this;
            while (true) {
                if (sc.TryLex(key, rbar_w, ref o)) {
                    return true;
                }
                if (!sc.HasCaller())
                    break;
                sc = sc.ToCaller();
            }
            if (key.Length >= 2 && key[1] == '*') {
                key = key.Remove(1,1);
                StashEnt bv;

                if (Kernel.currentGlobals.TryGetValue("\x8::GLOBAL" + key, out bv) ||
                        Kernel.currentGlobals.TryGetValue("\x9::PROCESS" + key, out bv)) {
                    if (rbar_w) { bv.v = o; } else { o = bv.v; }
                    return true;
                }
            }
            return false;
        }

        bool TryLex(string key, bool rbar_w, ref Variable o) {
            Frame f = (Frame)p1;
            if (p2 > 0) {
                int ix = f.info.GetInlineSlot(f.ip, key, p2);
                if (ix < 0) return false;

                if (rbar_w) f.SetDynamic(ix, o);
                else o = (Variable)f.GetDynamic(ix);
            }
            else {
                LexInfo li;
                if (!f.info.dylex.TryGetValue(key, out li)) return false;

                if (rbar_w) li.Set(f, o);
                else o = (Variable)li.Get(f);
            }
            return true;
        }

        public static P6any MakeCLR_WHO(string name) {
            StashCursor sc = default(StashCursor);
            sc.type = CLR;
            sc.p1 = name;
            P6any who = Kernel.BoxRaw(sc, Kernel.PseudoStashMO);
            who.SetSlot("name", Kernel.BoxAnyMO(name, Kernel.StrMO));
            return who;
        }

        void Core(string key, bool final, out StashCursor sc, out Variable v,
                Variable bind_to) {
            v = null;
            sc = this;
            if (type == DYNA) {
                // DYNAMIC::{key}, no special names used
                v = bind_to;
                if (TryDynamic(key, (bind_to != null), ref v)) {
                    if (bind_to != null) return;
                    goto have_v;
                }
                if (bind_to != null)
                    throw new NieczaException("No slot to bind");
                v = Kernel.AnyMO.typeVar;
                goto have_v;
            }
            else if (type == CLR) {
                if (Kernel.SaferMode)
                    throw new NieczaException("CLR objects may not be used directly in safe mode");
                if (bind_to != null)
                    throw new NieczaException("Cannot bind interop namespaces");
                v = CLRWrapperProvider.GetNamedWrapper((string)p1 + "." + key).typeVar;
                goto have_v;
            }
            else if (type == WHO) {
                // only special type is PARENT, maybe not even that?
                P6any who = (P6any) p1;
                Variable whov = Kernel.NewROScalar(who);
                Variable keyv = Kernel.BoxAnyMO(key, Kernel.StrMO);
                if (bind_to != null) {
                    v = who.mo.mro_bind_key.Bind(whov, keyv, bind_to);
                    return;
                }
                v = who.mo.mro_at_key.Get(whov, keyv);

                if (final) return;

                if (v.rw && !v.Fetch().IsDefined()) {
                    Variable vname = Kernel.RunInferior(who.InvokeMethod(
                        Kernel.GetInferiorRoot(), "name",
                        new Variable[] { whov }, null));
                    string name = vname.Fetch().mo.mro_raw_Str.Get(vname);
                    P6any new_who = Kernel.BoxRaw(name + "::" + key, Kernel.StashMO);
                    who.mo.mro_bind_key.Bind(whov, keyv,
                        MakePackage(key, new_who));
                    sc.p1 = new_who;
                    return;
                }
                else if (v.rw || v.Fetch().IsDefined()) {
                    throw new NieczaException(key + " does not point to a package");
                }
                else {
                    sc.p1 = v.Fetch().mo.who;
                    return;
                }
            }
            else if (type == ROOT) {
                // semantic root, handles most of the special names
                if (key == "OUR") {
                    v = ToInfo().cur_pkg.typeVar;
                    goto have_v;
                } else if (key == "GLOBAL") {
                    sc.p1 = Kernel.GetVar("", "GLOBAL").v.Fetch();
                    sc.type = WHO;
                    goto have_sc;
                } else if (key == "PROCESS") {
                    sc.p1 = Kernel.GetVar("", "PROCESS").v.Fetch();
                    sc.type = WHO;
                    goto have_sc;
                } else if (key == "UNIT" || key == "OUTER" ||
                        key == "SETTING" || key == "CALLER") {
                    StashCursor n = sc;
                    n.type = LEX;
                    n.Core(key, final, out sc, out v, bind_to);
                    return;
                } else if (key == "CORE") {
                    sc.type = LEX;
                    while (sc.ToInfo().unit == null ||
                            sc.ToInfo().unit.name != "CORE") sc = sc.ToOuter();
                    goto have_sc;
                } else if (key == "MY") {
                    sc.type = LEX;
                    goto have_sc;
                } else if (key == "COMPILING") {
                    throw new NieczaException("Cannot use COMPILING outside BEGIN scope");
                } else if (key == "DYNAMIC") {
                    sc.type = DYNA;
                    goto have_sc;
                } else if (key == "CLR") {
                    sc.type = CLR;
                    sc.p1 = "";
                    goto have_sc;
                } else {
                    v = bind_to;
                    if (TryLexOut(key, bind_to != null, ref v)) {
                        if (bind_to != null) return;
                        goto have_v;
                    }
                    StashCursor n = default(StashCursor);
                    n.type = WHO;
                    n.p1 = (key == "PARENT" || key.Length > 0 &&
                            "$&@%".IndexOf(key[0]) >= 0)
                        ? ToInfo().cur_pkg.who
                        : Kernel.GetVar("", "GLOBAL").v.Fetch();
                    n.Core(key, final, out sc, out v, bind_to);
                    return;
                }
            }
            else if (type == LEX) {
                if (key == "OUTER") {
                    sc = sc.ToOuter();
                    goto have_sc;
                }
                else if (key == "UNIT") {
                    while (sc.p2 > 0 || (sc.ToInfo().special &
                                SubInfo.MAINLINE) == 0)
                        sc = sc.ToOuter();
                    goto have_sc;
                }
                else if (key == "CALLER") {
                    sc = sc.ToCaller();
                    goto have_sc;
                }
                else if (key == "SETTING") {
                    while (sc.p2 > 0 || (sc.ToInfo().special &
                                SubInfo.MAINLINE) == 0)
                        sc = sc.ToOuter();
                    sc = sc.ToOuter();
                    goto have_sc;
                }
                else {
                    v = bind_to;
                    if (TryLexOut(key, bind_to != null, ref v)) {
                        if (bind_to != null) return;
                        goto have_v;
                    }
                    if (bind_to != null)
                        throw new NieczaException("No slot to bind");
                    v = Kernel.AnyMO.typeVar;
                    goto have_v;
                }
            }
            else {
                throw new NieczaException("corrupt StashCursor");
            }

have_sc:
            if (!final) return;
            if (bind_to != null)
                throw new NieczaException("cannot bind a pseudo package");
            {
                P6any who = Kernel.BoxRaw(this, Kernel.PseudoStashMO);
                who.SetSlot("name", Kernel.BoxAnyMO(key, Kernel.StrMO));
                v = MakePackage(key, who);
            }
            return;

have_v:
            if (final) return;
            if (v.rw || v.Fetch().IsDefined())
                throw new NieczaException(key + " is not a stash");
            sc.type = WHO;
            sc.p1 = v.Fetch().mo.who;
            return;
        }

        internal Variable Raw(string key, Variable bind_to) {
            Variable r;
            StashCursor sc;
            Core(key, true, out sc, out r, bind_to);
            return r;
        }

        public Variable Indirect(string key, bool bind_ro, Variable bind_to) {
            StashCursor sc = this;
            StashCursor r;
            Variable v;
            int ix1 = 0;
            string sigil = "";
            string last = "ROOT";
            while (true) {
                int ix2 = key.IndexOf("::", ix1);
                if (ix2 < 0) {
                    key = key.Substring(ix1);
                    break;
                }
                string elt = key.Substring(ix1, ix2 - ix1);
                while (elt.Length > 0 && "$&@%?=.!*~".IndexOf(elt[0]) >= 0) {
                    sigil = sigil + elt.Substring(0,1);
                    elt = elt.Substring(1);
                }
                ix1 = ix2+2;

                if (elt != "") {
                    sc.Core(elt, false, out r, out v, null);
                    last = elt;
                    sc = r;
                }
            }
            key = sigil + key;
            if (key == "") {
                if (bind_to != null)
                    throw new NieczaException("Cannot bind to a stash");
                if (sc.type == WHO)
                    return Kernel.NewROScalar((P6any) sc.p1);
                P6any who = Kernel.BoxRaw(sc, Kernel.PseudoStashMO);
                who.SetSlot("name", Kernel.BoxAnyMO(last, Kernel.StrMO));
                return Kernel.NewROScalar(who);
            }
            if (bind_to != null) {
                bool list = key != "" && (key[0] == '@' || key[0] == '%');
                bind_to = Kernel.NewBoundVar(list ? Kernel.NBV_LIST :
                    bind_ro ? Kernel.NBV_RO : Kernel.NBV_RW, Kernel.MuMO,
                    bind_to); // XXX should use types maybe?
            }
            sc.Core(key, true, out r, out v, bind_to);
            return v;
        }
    }

    // A bunch of stuff which raises big circularity issues if done in the
    // setting itself.
    public class Kernel {
        // not listed: BEGIN, CHECK (cannot be implemented in this model),
        // START (desugared using state), FIRST, NEXT, LAST (use masak code)
        public const int PHASER_INIT = 0;
        public const int PHASER_END = 1;
        public const int PHASER_UNIT_INIT = 2;
        public const int PHASER_KEEP = 3;
        public const int PHASER_UNDO = 4;
        public const int PHASER_LEAVE = 5;
        public const int PHASER_ENTER = 6;
        public const int PHASER_PRE = 7;
        public const int PHASER_POST = 8;
        public const int PHASER_CATCH = 9;
        public const int PHASER_CONTROL = 10;
        public const int PHASER_TYPES = 11;

        // XXX do lifo
        public static void FirePhasers(RuntimeUnit ru, int i, bool lifo) {
            foreach (SubInfo z in ru.our_subs) {
                if (z.phaser == i && z.protosub != null) {
                    RunInferior(z.protosub.Invoke(GetInferiorRoot(),
                        Variable.None, null));
                }
            }
        }

        internal static HashSet<string> ModulesStarted = new HashSet<string>();
        internal static Dictionary<string,RuntimeUnit> ModulesFinished =
            new Dictionary<string,RuntimeUnit>();

        public static Variable BootModule(string name, DynBlockDelegate dgt) {
            if (ModulesFinished.ContainsKey(name))
                return AnyMO.typeVar;
            if (ModulesStarted.Contains(name))
                throw new NieczaException("Recursive module graph detected at " + name + ": " + JoinS(" ", ModulesStarted));
            ModulesStarted.Add(name);
            Variable r = Kernel.RunInferior(Kernel.GetInferiorRoot().
                    MakeChild(null, new SubInfo("boot-" + name, dgt), AnyP));
            if (!ModulesFinished.ContainsKey(name))
                ModulesFinished[name] = null;
            return r;
        }

        public static void DoRequire(string name) {
            if (ModulesFinished.ContainsKey(name))
                return;
            Assembly a = Assembly.Load(name);
            Type t = a.GetType(name);
            if (t == null) throw new NieczaException("Load module must have a type of the same name");
            MethodInfo mi = t.GetMethod("BOOT");
            if (mi == null) throw new NieczaException("Load module must have a BOOT method");
            BootModule(name, delegate (Frame fr) {
                return (Frame) mi.Invoke(null, new object[] { fr });
            });
        }

        public static T UnboxAny<T>(P6any o) {
            return ((BoxObject<T>)o).value;
        }

        public static Frame Take(Frame th, Variable payload) {
            Frame c = th;
            while (c != null && c.coro_return == null)
                c = c.caller;
            if (c == null)
                return Kernel.Die(th, "used take outside of a coroutine");

            Frame r = c.coro_return;
            c.coro_return = c;
            r.LexicalBind("$*nextframe", NewROScalar(th));
            r.resultSlot = payload;
            th.resultSlot = payload;
            return r;
        }

        public static Frame CoTake(Frame th, Frame from) {
            Frame c = from;
            while (c != null && c.coro_return == null)
                c = c.caller;
            if (c.coro_return != c)
                return Kernel.Die(th, "Attempted to re-enter abnormally exitted or running coroutine");
            c.coro_return = th;

            return from;
        }

        static SubInfo dogather_SI = new SubInfo("KERNEL dogather", dogather);
        static Frame dogather(Frame th) {
            if (th.ip == 0) {
                th.ip = 1;
                return ((P6any)th.lex0).Invoke(th, Variable.None, null);
            } else {
                return Take(th, Kernel.EMPTYP.mo.typeVar);
            }
        }

        public static Frame GatherHelper(Frame th, P6any sub) {
            Frame n = th.MakeChild(null, dogather_SI, AnyP);
            n.lex0 = sub;
            n.MarkSharedChain();
            n.coro_return = n;
            th.resultSlot = n;
            return th;
        }

        private static SubInfo SubInvokeSubSI = new SubInfo("Sub.postcircumfix:<( )>", SubInvokeSubC);
        private static Frame SubInvokeSubC(Frame th) {
            Variable[] post;
            post = new Variable[th.pos.Length - 1];
            Array.Copy(th.pos, 1, post, 0, th.pos.Length - 1);
            return CodeMO.mro_INVOKE.Invoke((P6opaque)th.pos[0].Fetch(),
                    th.Return(), post, th.named);
        }

        private static SubInfo JunctionFallbackSI = new SubInfo("Junction.FALLBACK", JunctionFallbackC);
        private static Frame JunctionFallbackC(Frame th) {
            if (th.ip == 0) {
                if (!th.pos[0].Fetch().IsDefined())
                    return Kernel.Die(th, "Cannot autothread an undefined junction");
                P6opaque jo = (P6opaque)th.pos[0].Fetch();
                th.lex0 = Kernel.UnboxAny<Variable[]>((P6any)jo.slots[1]);
                th.lex1 = new Variable[((Variable[])th.lex0).Length];
                th.lex2 = jo.slots[0];
                th.lex3 = th.pos[1].Fetch().mo.mro_raw_Str.Get(th.pos[1]);
                th.lex4 = new Variable[th.pos.Length - 1];
                Array.Copy(th.pos, 2, (Variable[])th.lex4, 1, th.pos.Length-2);
            }

            Variable[] src = (Variable[]) th.lex0;
            Variable[] dst = (Variable[]) th.lex1;
            if (th.ip > 0)
                dst[th.ip - 1] = (Variable)th.resultSlot;

            if (th.ip == dst.Length) {
                P6opaque nj = new P6opaque(Kernel.JunctionMO);
                nj.slots[0] = th.lex2;
                nj.slots[1] = Kernel.BoxRaw(dst, Kernel.ParcelMO);
                th.caller.resultSlot = Kernel.NewROScalar(nj);
                return th.Return();
            }

            Variable[] npos = (Variable[]) th.lex4;
            npos[0] = src[th.ip++];

            return npos[0].Fetch().InvokeMethod(th, (string)th.lex3,
                    npos, th.named);
        }

        public static Frame Die(Frame caller, string msg) {
            return SearchForHandler(caller, SubInfo.ON_DIE, null, -1, null,
                    BoxAnyMO<string>(msg, StrMO));
        }

        public static P6any SigSlurpCapture(Frame caller) {
            P6any nw = new P6opaque(CaptureMO);
            nw.SetSlot("positionals", caller.pos);
            nw.SetSlot("named", caller.named);
            caller.named = null;
            return nw;
        }

        [CORESaved] public static STable PairMO;
        [CORESaved] public static STable EnumMapMO;
        [CORESaved] public static STable CallFrameMO;
        [CORESaved] public static STable CaptureMO;
        [CORESaved] public static STable GatherIteratorMO;
        [CORESaved] public static STable IterCursorMO;
        [CORESaved] public static P6any NilP;
        [CORESaved] public static P6any AnyP;
        [CORESaved] public static P6any ArrayP;
        [CORESaved] public static P6any EMPTYP;
        [CORESaved] public static P6any HashP;
        [CORESaved] public static P6any IteratorP;
        [CORESaved] public static STable RealMO;
        [CORESaved] public static STable IntegralMO;
        [CORESaved] public static STable JunctionMO;
        [CORESaved] public static STable LabelMO;
        [CORESaved] public static STable AnyMO;
        [CORESaved] public static STable IteratorMO;
        [CORESaved] public static STable ScalarMO;
        [CORESaved] public static STable StashMO;
        [CORESaved] public static STable ClassHOWMO;
        [CORESaved] public static STable PseudoStashMO;
        [CORESaved] public static STable GrammarMO;
        [CORESaved] public static STable CodeMO;
        [CORESaved] public static STable WhateverCodeMO;
        [CORESaved] public static STable RoutineMO;
        [CORESaved] public static STable SubMO;
        [CORESaved] public static STable SubmethodMO;
        [CORESaved] public static STable MethodMO;
        [CORESaved] public static STable BlockMO;
        [CORESaved] public static STable RegexMO;
        [CORESaved] public static STable StrMO;
        [CORESaved] public static STable NumMO;
        [CORESaved] public static STable IntMO;
        [CORESaved] public static STable RatMO;
        [CORESaved] public static STable FatRatMO;
        [CORESaved] public static STable ComplexMO;
        [CORESaved] public static STable ArrayMO;
        [CORESaved] public static STable CursorMO;
        [CORESaved] public static STable MatchMO;
        [CORESaved] public static STable ParcelMO;
        [CORESaved] public static STable ListMO;
        [CORESaved] public static STable HashMO;
        [CORESaved] public static STable BoolMO;
        [CORESaved] public static STable MuMO;
        [CORESaved] public static P6any StashP;

        [CORESaved] public static Variable TrueV;
        [CORESaved] public static Variable FalseV;

        public static P6any MakeSub(SubInfo info, Frame outer) {
            P6opaque n = new P6opaque(info.mo ?? CodeMO, 2);
            n.slots[0] = outer;
            if (outer != null) outer.MarkShared();
            n.slots[1] = info;
            return n;
        }

        public class MMDParameter {
            public STable type;
            public bool constrained;
            public bool required;

            public static MMDParameter TOP = new MMDParameter();
            public static MMDParameter BOTTOM = new MMDParameter();

            // XXX Should requiredness be factored in?
            // 2: narrower 0: tied 1: less narrow 3: incomparable
            // by design these can be ORed
            public int IsNarrowerThan(MMDParameter other) {
                if (other == TOP) return (this == TOP) ? 0 : 2;
                if (other == BOTTOM) return (this == BOTTOM) ? 0 : 1;
                if (this == TOP) return 1;
                if (this == BOTTOM) return 2;

                bool k1 = type.HasMRO(other.type);
                bool k2 = other.type.HasMRO(type);
                if (k1 && !k2) return 2;
                if (k2 && !k1) return 1;

                if (constrained && !other.constrained) return 2;
                if (other.constrained && !constrained) return 1;

                return 0;
            }
        }

        public class MMDCandidateLongname {
            public P6any impl;
            public int tien;
            public SubInfo info;

            public List<MMDParameter> pos;
            public Dictionary<string,MMDParameter> nam;

            public bool slurpy_pos;
            public bool slurpy_nam;
            public bool extra_constraints;

            public bool IsNarrowerThan(MMDCandidateLongname other) {
                int narrower = 0;

                for (int i = 0; ; i++) {
                    MMDParameter tp = (i < pos.Count) ? pos[i] :
                        (slurpy_pos ? MMDParameter.TOP : MMDParameter.BOTTOM);
                    MMDParameter op = (i < other.pos.Count) ? other.pos[i] :
                        (other.slurpy_pos ? MMDParameter.TOP : MMDParameter.BOTTOM);
                    narrower |= tp.IsNarrowerThan(op);
                    if (i >= pos.Count && i >= other.pos.Count) break;
                }

                List<string> ns = new List<string>(nam.Keys);
                foreach (string s in other.nam.Keys)
                    if (!nam.ContainsKey(s)) ns.Add(s);
                foreach (string s in ns) {
                    MMDParameter tp = nam.ContainsKey(s) ? nam[s] :
                        (slurpy_nam ? MMDParameter.TOP : MMDParameter.BOTTOM);
                    MMDParameter op = other.nam.ContainsKey(s) ? other.nam[s] :
                        (other.slurpy_nam ? MMDParameter.TOP : MMDParameter.BOTTOM);
                    narrower |= tp.IsNarrowerThan(op);
                }

                if (slurpy_nam && !other.slurpy_nam) narrower |= 1;
                if (!slurpy_nam && other.slurpy_nam) narrower |= 2;

                if (narrower == 0 || narrower == 3)
                    return tien < other.tien;

                return (narrower == 2);
            }

            public MMDCandidateLongname(P6any impl, int tien) {
                this.impl = impl;
                this.tien = tien;
                info = (SubInfo) impl.GetSlot("info");
                int ct = info.sig_i.Length / SubInfo.SIG_I_RECORD;

                pos = new List<MMDParameter>();
                nam = new Dictionary<string,MMDParameter>();
                int rix = 0;

                for (int ix = 0; ix < ct; ix++) {
                    int flags = info.sig_i[ix*SubInfo.SIG_I_RECORD +
                        SubInfo.SIG_I_FLAGS];
                    int nnames = info.sig_i[ix*SubInfo.SIG_I_RECORD +
                        SubInfo.SIG_I_NNAMES];
                    int rbase = rix;
                    rix += (nnames + 1);
                    MMDParameter p = new MMDParameter();
                    p.required = ((flags & (SubInfo.SIG_F_OPTIONAL | SubInfo.SIG_F_HASDEFAULT)) == 0);
                    p.constrained = false;
                    p.type = AnyMO;
                    if ((flags & SubInfo.SIG_F_HASDEFAULT) != 0) rix++;
                    if ((flags & SubInfo.SIG_F_HASTYPE) != 0)
                        p.type = (STable) info.sig_r[rix++];

                    // XXX The long name model does not represent the full
                    // diversity of Perl 6 parameters, instead restricting
                    // them to 'only positional' or '1 name'
                    if (nnames > 0 && (flags & SubInfo.SIG_F_POSITIONAL) == 0) {
                        if (nnames == 1) {
                            nam[(string)info.sig_r[rbase+1]] = p;
                        } else {
                            slurpy_nam = true;
                            extra_constraints = true;
                        }
                    } else if ((flags & SubInfo.SIG_F_SLURPY_PCL) != 0) {
                        slurpy_pos = true;
                    } else if ((flags & SubInfo.SIG_F_SLURPY_CAP) != 0) {
                        slurpy_pos = true;
                        slurpy_nam = true;
                    } else if ((flags & SubInfo.SIG_F_SLURPY_POS) != 0) {
                        slurpy_pos = true;
                    } else if ((flags & SubInfo.SIG_F_SLURPY_NAM) != 0) {
                        slurpy_nam = true;
                    } else if ((flags & SubInfo.SIG_F_POSITIONAL) != 0) {
                        pos.Add(p);
                    }
                }
            }

            private static long unique;
            public static long get_unique() {
                return Interlocked.Increment(ref unique);
            }

            public string LongName() {
                List<string> bits = new List<string>();

                foreach (MMDParameter p in pos) {
                    string n = p.type.name;
                    if (!p.required) n += "?";
                    if (p.constrained)
                        n += " where { ... " + get_unique() + " }";
                    bits.Add(n);
                }

                if (slurpy_pos) bits.Add("*@_");

                List<string> names = new List<string>(nam.Keys);
                names.Sort();
                foreach (string nm in names) {
                    MMDParameter p = nam[nm];
                    string b = p.type.name + " :$" + nm;
                    if (p.required) b += "!";
                    if (p.constrained)
                        b += " where { ... " + get_unique() + " }";
                    bits.Add(b);
                }

                if (slurpy_nam) bits.Add("*%_");
                string full = JoinS(", ", bits);
                if (extra_constraints)
                    full += ";; |$c where { ... " + get_unique() + " }";
                return full;
            }
        }

        // This is a little odd.  Pending full understanding of the
        // rules it aims more for Rakudo compatibility than anything
        // else.
        private static List<P6any> SortCandidates(P6any[] raw) {
            int[] links = new int[raw.Length * raw.Length * 2];
            int ap = 0;
            int[] heads = new int[raw.Length * 2];
            int[] orig = new int[raw.Length];
            MMDCandidateLongname[] lns = new MMDCandidateLongname[raw.Length];
            int nlns = 0;
            int tien = 0;

            for (int i = 0; i < raw.Length; i++) {
                if (raw[i] == null) {
                    tien++;
                } else {
                    lns[nlns] = new MMDCandidateLongname(raw[i], tien);
                    orig[nlns] = i;
                    heads[2*nlns] = -1;
                    nlns++;
                }
            }

            for (int i = 0; i < nlns; i++) {
                for (int j = 0; j < nlns; j++) {
                    if (lns[i].IsNarrowerThan(lns[j])) {
                        //Console.WriteLine("{0} < {1}", lns[i].LongName(), lns[j].LongName());
                        heads[2*j+1]++;
                        links[ap] = heads[2*i];
                        links[ap+1] = j;
                        heads[2*i] = ap;
                        ap += 2;
                    }
                }
            }

            List<P6any> outp = new List<P6any>();

            int k = nlns;
            while (k != 0) {
                int d = 0;
                for (int i = 0; i < nlns; i++) {
                    if (heads[2*i+1] != 0) continue;
                    heads[2*i+1] = -1;
                    d++;
                }
                for (int i = 0; i < nlns; i++) {
                    if (heads[2*i+1] != -1) continue;
                    heads[2*i+1] = -2;

                    for (int j = heads[2*i]; j >= 0; j = links[j]) {
                        heads[2*links[j+1]+1]--;
                    }
                    // prevent constrained candidates from being part of a tie
                    if (lns[i].extra_constraints) outp.Add(null);
                    outp.Add(raw[orig[i]]);
                    if (lns[i].extra_constraints) outp.Add(null);
                    k--;
                }
                if (d == 0 && k != 0) {
                    throw new NieczaException("Partial order wedged");
                }
                outp.Add(null);
            }

            return outp;
        }

        public static Frame TypeDispatcher(Frame th, bool tailcall) {
            Frame dth = th;
            while (dth.info.param == null ||
                    (dth.info.param[0] as P6any[]) == null) dth = dth.outer;

            //Console.WriteLine("---");
            DispatchEnt root = new DispatchEnt();
            DispatchEnt ptr  = root;

            List<P6any> sp = SortCandidates(dth.info.param[0] as P6any[]);

            // XXX I think this is a harmless race
            //MMDCandidate[] cs = dth.info.param1 as MMDCandidate[];
            //if (cs == null)
            //    dth.info.param1 = cs = MMDAnalyze(dth.info.param0 as P6any[]);
            int tie_state = 0;
            // 0: seen nothing  1: after first group  2: an item in first group
            foreach (P6any p in sp) {
                if (p == null) {
                    if (tie_state == 2) tie_state = 1;
                    //Console.WriteLine(".");
                    continue;
                }
                //Console.WriteLine((new MMDCandidateLongname(p,0)).LongName());
                SubInfo si = (SubInfo)p.GetSlot("info");
                Frame   o  = (Frame)p.GetSlot("outer");
                if (si.Binder(th, o, p, dth.pos, dth.named, true,
                            null) == null ) {
                    //Console.WriteLine("NOT BINDABLE");
                } else {
                    if (tie_state == 0) tie_state = 2;
                    else if (tie_state == 2)
                        return Kernel.Die(th, "Ambiguous dispatch for " + dth.info.name);
                    //Console.WriteLine("BINDABLE");
                    ptr.next = new DispatchEnt(null, p);
                    ptr = ptr.next;
                }
            }

            root = root.next;
            if (root == null)
                return Kernel.Die(th, "No matching candidates to dispatch for " + dth.info.name);

            if (tailcall) th = th.Return();
            return root.info.Binder(th, root.outer, root.ip6,
                    dth.pos, dth.named, false, root);
        }

        private static Frame StandardTypeProtoC(Frame th) {
            return TypeDispatcher(th, true);
        }

        public static P6any MakeDispatcher(string name, P6any proto, P6any[] cands) {
            //string s1 = "Dispatch";
            //foreach (P6any s in cands)
            //    s1 += ", " + ((SubInfo)s.GetSlot("info")).name;
            //Console.WriteLine("MakeDispatcher: {0}", s1);

            if (proto != null && proto.mo.name == "Regex") goto ltm;
            for (int i = 0; i < cands.Length; i++) {
                if (cands[i] == null) continue;
                if (cands[i].mo.name == "Regex") goto ltm;
                break;
            }

            SubInfo si = new SubInfo(name, StandardTypeProtoC);
            si.param = new object[] { cands, null };
            return Kernel.MakeSub(si, null);
ltm:
            List<P6any> lp = new List<P6any>();
            foreach (P6any p in cands)
                if (p != null) lp.Add(p);
            return Lexer.MakeDispatcher(name, lp.ToArray());
        }

        public static bool SaferMode;

        private static Frame SaferTrap(Frame th) {
            return Die(th, th.info.name + " may not be used in safe mode");
        }

        public static void CheckUnsafe(SubInfo info) {
            if (SaferMode)
                info.code = SaferTrap;
        }
        public static Variable BoxAny<T>(T v, P6any proto) {
            if (proto == BoolMO.typeObject) {
                if (v is bool)
                    return ((bool) (object) v) ? TrueV : FalseV;
                else
                    return ((int) (object) v) != 0 ? TrueV : FalseV;
            }
            return NewROScalar(new BoxObject<T>(v, ((P6opaque)proto).mo));
        }

        public static void SetBox<T>(P6any obj, T v) {
            ((BoxObject<T>) obj).value = v;
        }

        public static Variable BoxAnyMO<T>(T v, STable proto) {
            if (proto == BoolMO) {
                if (v is bool)
                    return ((bool) (object) v) ? TrueV : FalseV;
                else
                    return ((int) (object) v) != 0 ? TrueV : FalseV;
            }
            return NewROScalar(new BoxObject<T>(v, proto));
        }

        public static P6any BoxRaw<T>(T v, STable proto) {
            return new BoxObject<T>(v, proto);
        }

        // check whence before calling
        public static void Vivify(Variable v) {
            ViviHook w = v.whence;
            v.whence = null;
            w.Do(v);
        }

        public static Variable Decontainerize(Variable rhs) {
            if (!rhs.rw) return rhs;
            P6any v = rhs.Fetch();
            return new SimpleVariable(false, rhs.islist, v.mo, null, v);
        }

        public const int NBV_RO = 0;
        public const int NBV_RW = 1;
        public const int NBV_LIST = 2;
        public static Variable NewBoundVar(int mode, STable type, Variable rhs) {
            bool rw = rhs.rw && (mode & NBV_RW) != 0;
            // we always have to fetch, because of subsets (XXX?)
            P6any rhso = rhs.Fetch();
            if (!rhso.Does(type))
                throw new NieczaException("Nominal type check failed in nonparameter binding; got " + rhso.mo.name + ", needed " + type.name);

            if (rw) {
                // working RW bind implies !rhs.islist, !islist; will return
                // rhs if successful
                if (rhs.whence != null) Vivify(rhs);
                return rhs;
            }

            bool islist = (mode & NBV_LIST) != 0;
            // if source is !rw, we may not need to fetch it
            if (!rhs.rw && islist == rhs.islist)
                return rhs;

            return new SimpleVariable(false, islist, rhso.mo, null, rhso);
        }

        public static Variable Assign(Variable lhs, Variable rhs) {
            if (!lhs.islist) {
                if (!lhs.rw)
                    throw new NieczaException("assigning to readonly value");

                lhs.Store(rhs.Fetch());
            } else {
                lhs.Fetch().mo.mro_LISTSTORE.Get(lhs, rhs);
            }
            return lhs;
        }

        // ro, not rebindable
        public static Variable NewROScalar(P6any obj) {
            return new SimpleVariable(false, false, obj.mo, null, obj);
        }

        public static Variable NewRWScalar(STable t, P6any obj) {
            return new SimpleVariable(true, false, t, null, obj);
        }

        public static Variable NewMuScalar(P6any obj) {
            return new SimpleVariable(true, false, MuMO, null, obj);
        }

        public static Variable NewTypedScalar(STable t) {
            if (t == null)
                return new SimpleVariable(true, false, MuMO, null,
                        AnyMO.typeObject);

            return new SimpleVariable(true, false, t, null, t.initObject);
        }

        public static Variable NewRWListVar(P6any container) {
            return new SimpleVariable(false, true, container.mo, null,
                    container);
        }

        public static VarDeque SlurpyHelper(Frame th, int from) {
            VarDeque lv = new VarDeque();
            for (int i = from; i < th.pos.Length; i++) {
                lv.Push(th.pos[i]);
            }
            return lv;
        }

        public static VarDeque IterCopyElems(VarDeque vals) {
            VarDeque nv = new VarDeque();
            for (int i = 0; i < vals.Count(); i++)
                nv.Push(NewMuScalar(vals[i].Fetch()));
            return nv;
        }

        public static string[] commandArgs;

        public static VarDeque SortHelper(Frame th, P6any cb, VarDeque from) {
            Variable[] tmp = from.CopyAsArray();
            Array.Sort(tmp, delegate (Variable v1, Variable v2) {
                Variable v = RunInferior(cb.Invoke(GetInferiorRoot(),
                        new Variable[] { v1, v2 }, null));
                return (int)v.Fetch().mo.mro_raw_Numeric.Get(v);
            });
            return new VarDeque(tmp);
        }

        public static Variable ContextHelper(Frame th, string name, int up) {
            object rt;
            uint m = SubInfo.FilterForName(name);
            while (th != null) {
                if (up <= 0 && th.TryGetDynamic(name, m, out rt)) {
                    return (Variable)rt;
                }
                th = th.caller;
                up--;
            }
            name = name.Remove(1,1);
            StashEnt v;

            if (currentGlobals.TryGetValue("\x8::GLOBAL" + name, out v)) {
                return v.v;
            } else if (currentGlobals.TryGetValue("\x9::PROCESS" + name, out v)) {
                return v.v;
            } else {
                return AnyMO.typeVar;
            }
        }

        public static void SetStatus(Frame th, string name, Variable v) {
            th = th.caller;
            while (true) {
                string n = th.info.name;
                // Mega-Hack: These functions wrap stuff and should
                // propagate $/
                if (n == "CORE infix:<~~>" || n == "ExitRunloop" || n == "KERNEL AutoThreadSub") {
                    th = th.caller;
                    continue;
                }
                break;
            }
            th.LexicalBind(name, v);
        }

        public static Frame DefaultNew(Frame th, P6any proto, VarHash args) {
            P6opaque n;
            P6how.AttrInfo[] prog;
            int i;
            if (th.lex9 == null) {
                th.lex9 = n = new P6opaque(((P6opaque)proto).mo);
                i = 0;
                prog = n.mo.init_program;
            } else {
                n = (P6opaque) th.lex9;
                prog = n.mo.init_program;
                i = th.lexi0;
                goto value;
            }

again:      if (i == prog.Length) {
                th.caller.resultSlot = NewROScalar(n);
                return th.Return();
            }

            Variable vx;
            if ((prog[i].flags & P6how.A_PUBLIC) != 0 &&
                    args.TryGetValue(prog[i].name, out vx)) {
                args.Remove(prog[i].name);
                th.resultSlot = vx;
            } else if (prog[i].init == null) {
                th.resultSlot = null;
            } else if (prog[i].name == null) {
                P6any init = prog[i].init;
                th.lexi0 = i;

                SubInfo si = (SubInfo) init.GetSlot("info");
                VarHash build_args = new VarHash();
                int ic = 0;
                int oc = 0;
                while (ic < si.sig_i.Length) {
                    int fl = si.sig_i[ic + SubInfo.SIG_I_FLAGS];
                    int nn = si.sig_i[ic + SubInfo.SIG_I_NNAMES];
                    ic += SubInfo.SIG_I_RECORD;

                    for (int j = 0; j < nn; j++) {
                        string name = (string) si.sig_r[oc+j+1];
                        if (args.ContainsKey(name)) {
                            build_args[name] = args[name];
                            args.Remove(name);
                        }
                    }

                    oc += 1 + nn;
                    if ((fl & SubInfo.SIG_F_HASTYPE) != 0) oc++;
                    if ((fl & SubInfo.SIG_F_HASDEFAULT) != 0) oc++;
                }

                return init.Invoke(th, new Variable[] { NewROScalar(n) },
                    build_args);
            } else {
                P6any init = prog[i].init;
                th.lexi0 = i;
                return init.Invoke(th, Variable.None, null);
            }

value:      vx = (Variable) th.resultSlot;
            if (prog[i].name == null) {
                i++;
                goto again;
            }

            if ((prog[i].flags & ~P6how.A_PUBLIC) == 0) {
                if (prog[i].type == null)
                    n.SetSlot(prog[i].name, NewMuScalar(
                        vx != null ? vx.Fetch() : AnyMO.typeObject));
                else
                    n.SetSlot(prog[i].name, NewRWScalar(prog[i].type,
                        vx != null ? vx.Fetch() : prog[i].type.initObject));
            } else {
                Variable obj = (prog[i].flags & P6how.A_HASH) != 0 ?
                    CreateHash() : CreateArray();
                if (vx != null) Assign(obj, vx);
                n.SetSlot(prog[i].name, obj);
            }

            i++;
            goto again;
        }

        public static Frame PromoteToList(Frame th, Variable v) {
            if (!v.islist) {
                P6opaque lst = new P6opaque(Kernel.ListMO);
                lst.slots[0 /*items*/] = new VarDeque(v);
                lst.slots[1 /*rest*/ ] = new VarDeque();
                th.resultSlot = Kernel.NewRWListVar(lst);
                return th;
            }
            P6any o = v.Fetch();
            if (o.mo.HasMRO(Kernel.ListMO)) {
                th.resultSlot = v;
                return th;
            }
            return o.InvokeMethod(th, "list", new Variable[] { v }, null);
        }

        // An Iterator is a VarDeque, where each element is either:
        //   an IterCursor, representing work to be done lazily
        //   a value with islist, representing a flattenable sublist
        //   anything else, representing that value

        // Laziness dictates that IterCursors not be reified until necessary,
        // and any infinite or I/O-bearing tasks be wrapped in them.  Calls
        // to List.iterator, however, may be assumed cheap and done eagerly.

        public static void IterToList(P6any list, VarDeque iter) {
            VarDeque items = new VarDeque();
            P6any item;
            while (iter.Count() != 0) {
                item = iter[0].Fetch();
                if (item.mo.HasMRO(IterCursorMO)) {
                    break;
                } else {
                    items.Push(iter.Shift());
                }
            }
            list.SetSlot("items", items);
            list.SetSlot("rest", iter);
        }

        public static VarDeque IterFlatten(VarDeque inq) {
            VarDeque outq = new VarDeque();
            Variable inq0v;
            P6any inq0;

again:
            if (inq.Count() == 0)
                return outq;
            inq0v = inq[0];
            inq0 = inq0v.Fetch();
            if (inq0v.islist) {
                inq.Shift();
                inq.UnshiftD(inq0.mo.mro_raw_iterator.Get(inq0v));
                goto again;
            }
            if (inq0.mo.HasMRO(IterCursorMO)) {
                Frame th = new Frame(null, null, IF_SI, Kernel.AnyP);
                th.lex0 = inq;
                P6opaque thunk = new P6opaque(Kernel.GatherIteratorMO);
                th.coro_return = th;
                thunk.slots[0] = NewMuScalar(th);
                thunk.slots[1] = NewMuScalar(AnyP);
                outq.Push(NewROScalar(thunk));
                return outq;
            }
            outq.Push(inq0v);
            inq.Shift();
            goto again;
        }

        private static SubInfo IF_SI = new SubInfo("iter_flatten", IF_C);
        private static Frame IF_C(Frame th) {
            VarDeque inq = (VarDeque) th.lex0;
            if (IterHasFlat(inq, true)) {
                return Take(th, inq.Shift());
            } else {
                return Take(th, NewROScalar(Kernel.EMPTYP));
            }
        }

        public static bool IterHasFlat(VarDeque iter, bool flat) {
            while (true) {
                if (iter.Count() == 0)
                    return false;
                Variable i0 = iter[0];
                if (i0.islist && flat) {
                    iter.Shift();
                    iter.UnshiftD(i0.Fetch().mo.mro_raw_iterator.Get(i0));
                    continue;
                }
                P6any i0v = i0.Fetch();
                if (i0v.mo.HasMRO(IterCursorMO)) {
                    iter.Shift();
                    iter.UnshiftN(i0v.mo.mro_raw_reify.Get(i0));
                    continue;
                }

                return true;
            }
        }

        public static Variable GetFirst(Variable lst) {
            if (!lst.islist) {
                return lst;
            }
            P6opaque dyl = lst.Fetch() as P6opaque;
            if (dyl == null) { goto slow; }
            if (dyl.mo != Kernel.ListMO) { goto slow; }
            VarDeque itemsl = (VarDeque) dyl.GetSlot("items");
            if (itemsl.Count() == 0) {
                VarDeque restl = (VarDeque) dyl.GetSlot("rest");
                if (restl.Count() == 0) {
                    return AnyMO.typeVar;
                }
                goto slow;
            }
            return itemsl[0];

slow:
            return RunInferior(lst.Fetch().InvokeMethod(
                        GetInferiorRoot(), "head", new Variable[] {lst}, null));
        }

        public static Variable CreateArray() {
            P6any v = new P6opaque(ArrayMO, 2);
            v.SetSlot("items", new VarDeque());
            v.SetSlot("rest", new VarDeque());
            return NewRWListVar(v);
        }

        public static Variable CreateHash() {
            P6any v = BoxRaw(new VarHash(), HashMO);
            return NewRWListVar(v);
        }

        public static Variable StashyMerge(Variable o, Variable n, string d1, string d2) {
            if (n.rw || n.islist) return o;
            if (o.rw || o.islist) return n;

            P6any oo = o.Fetch();
            P6any nn = n.Fetch();

            if (oo == nn) return o;

            if (!oo.IsDefined() && !nn.IsDefined() && oo.mo.who == nn.mo.who) {
                if (oo.mo.mo.isPackage) return n;
                if (nn.mo.mo.isPackage) return o;
            }

            throw new NieczaException("Funny merge failure " + d1 + "::" + d2 +
                    " (" + oo.mo.name + ", " + nn.mo.name + ")");
        }

        public static StashEnt GetVar(string who, string name) {
            StashEnt v;
            string key = (char)who.Length + who + name;

            if (currentGlobals.TryGetValue(key, out v))
                return v;
            else
                v = currentGlobals[key] = new StashEnt();


            if (name.StartsWith("@")) {
                v.v = CreateArray();
            } else if (name.StartsWith("%")) {
                v.v = CreateHash();
            } else {
                v.v = NewTypedScalar(null);
            }

            return v;
        }

        private static void Handler_Vonly(STable kl, string name,
                ContextHandler<Variable> cv, object cvu) {
            WrapHandler0(kl, name, cv, cvu);
        }

        private static void Handler_PandBox<T>(STable kl, string name,
                ContextHandler<T> cvu, STable box) {
            ContextHandler<Variable> cv = new CtxBoxify<T>(cvu, box);
            WrapHandler0(kl, name, cv, cvu);
        }

        private static void Handler_PandBoxInty(STable kl, string name,
                ContextHandler<double> cvu) {
            ContextHandler<Variable> cv = new CtxBoxifyInty(cvu);
            WrapHandler0(kl, name, cv, cvu);
        }

        private static void Handler_PandCont(STable kl, string name,
                ContextHandler<P6any> cvu) {
            ContextHandler<Variable> cv = new CtxContainerize(cvu);
            WrapHandler0(kl, name, cv, cvu);
        }

        private static void WrapHandler0(STable kl, string name,
                ContextHandler<Variable> cvb, object cvu) {
            DynBlockDelegate dbd = delegate (Frame th) {
                th.caller.resultSlot = ((ContextHandler<Variable>)
                        th.info.param[1]).Get((Variable)th.lex0);
                return th.Return();
            };
            SubInfo si = new SubInfo("KERNEL " + kl.name + "." + name, dbd);
            si.sig_i = new int[3] {
                SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL,
                0, 0 };
            si.sig_r = new object[1] { "self" };
            si.param = new object[] { cvu, cvb };
            kl.AddMethod(0, name, MakeSub(si, null));
        }

        private static void WrapHandler1(STable kl, string name,
                IndexHandler cv) {
            DynBlockDelegate dbd = delegate (Frame th) {
                IndexHandler fcv = (IndexHandler) th.info.param[1];
                th.caller.resultSlot = fcv.Get((Variable)th.lex0,
                        (Variable)th.lex1);
                return th.Return();
            };
            SubInfo si = new SubInfo("KERNEL " + kl.name + "." + name, dbd);
            si.sig_i = new int[6] {
                SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 0, 0,
                SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 1, 0
            };
            si.sig_r = new object[2] { "self", "$key" };
            si.param = new object[] { null, cv };
            kl.AddMethod(0, name, MakeSub(si, null));
        }

        private static void WrapPushy(STable kl, string name,
                PushyHandler cv) {
            DynBlockDelegate dbd = delegate (Frame th) {
                PushyHandler fcv = (PushyHandler) th.info.param[1];
                Variable[] fullpc = UnboxAny<Variable[]>(
                        ((Variable)th.lex1).Fetch());
                Variable[] chop = new Variable[fullpc.Length - 1];
                Array.Copy(fullpc, 1, chop, 0, chop.Length);
                th.caller.resultSlot = fcv.Invoke((Variable)th.lex0, chop);
                return th.Return();
            };
            SubInfo si = new SubInfo("KERNEL " + kl.name + "." + name, dbd);
            si.sig_i = new int[6] {
                SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 0, 0,
                SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_SLURPY_PCL, 1, 0
            };
            si.sig_r = new object[2] { "self", "$args" };
            si.param = new object[] { null, cv };
            kl.AddMethod(0, name, MakeSub(si, null));
        }

        private static Variable DispIndexy(object[] p, VarHash n,
                Variable self, Variable index) {

            if (n == null) goto def;
            if (p[1] != null && n.ContainsKey("exists"))
                return ((IndexHandler)p[1]).Get(self, index);
            if (p[2] != null && n.ContainsKey("delete"))
                return ((IndexHandler)p[2]).Get(self, index);

            if (n.ContainsKey("k"))
                return new KeySlicer(0, p[0]).Get(self, index);
            if (n.ContainsKey("kv"))
                return new KeySlicer(1, p[0]).Get(self, index);
            if (n.ContainsKey("p"))
                return new KeySlicer(2, p[0]).Get(self, index);
            if (n.ContainsKey("BIND_VALUE"))
                return ((BindHandler)p[3]).Bind(self, index, n["BIND_VALUE"]);

def:        return ((IndexHandler)p[0]).Get(self, index);
        }

        private static void WrapIndexy(STable kl, string name,
                IndexHandler at, IndexHandler exist, IndexHandler del,
                BindHandler bind) {
            DynBlockDelegate dbd = delegate (Frame th) {
                th.caller.resultSlot = DispIndexy(th.info.param,
                    th.named, (Variable)th.lex0, (Variable)th.lex1);
                return th.Return();
            };

            SubInfo si = new SubInfo("KERNEL " + kl.name + "." + name, dbd);
            if (del != null) {
                si.sig_i = new int[24] {
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 0, 0,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 1, 0,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1
                };
                si.sig_r = new object[14] { "self", "$index", "exists", "exists", "delete", "delete", "k", "k", "kv", "kv", "p", "p", "BIND_VALUE", "BIND_VALUE" };
            } else {
                si.sig_i = new int[18] {
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 0, 0,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_POSITIONAL, 1, 0,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1,
                    SubInfo.SIG_F_RWTRANS | SubInfo.SIG_F_OPTIONAL, -1, 1
                };
                si.sig_r = new object[10] { "self", "$index", "k", "k", "kv", "kv", "p", "p", "BIND_VALUE", "BIND_VALUE" };
            }
            si.param = new object[] { at, exist, del, bind };
            kl.AddMethod(0, name, MakeSub(si, null));
        }

        private static SubInfo IRSI = new SubInfo("InstantiateRole", IRC);
        private static Frame IRC(Frame th) {
            switch (th.ip) {
                case 0:
                    {
                        string s = "";
                        th.lex0 = th.pos[0].Fetch().mo;
                        bool cache_ok = true;
                        Variable[] args;
                        P6any argv = th.pos[1].Fetch();
                        if (argv.mo == Kernel.ParcelMO) {
                            args = UnboxAny<Variable[]>(argv);
                        } else {
                            args = new Variable[] { th.pos[1] };
                        }
                        Variable[] to_pass = new Variable[args.Length];
                        for (int i = 0; i < args.Length; i++) {
                            P6any obj = args[i].Fetch();
                            to_pass[i] = NewROScalar(obj);
                            if (obj.mo == StrMO) {
                                string p = UnboxAny<string>(obj);
                                s += new string((char)p.Length, 1);
                                s += p;
                            } else { cache_ok = false; }
                        }
                        if (!cache_ok) {
                            return ((STable) th.lex0).mo.roleFactory.
                                Invoke(th.Return(), to_pass, null);
                        }
                        th.lex1 = s;
                        bool ok;
                        P6any r;
                        lock (th.lex0)
                            ok = ((STable) th.lex0).mo.instCache.
                                TryGetValue((string) th.lex1, out r);
                        if (ok) {
                            th.caller.resultSlot = NewROScalar(r);
                            return th.Return();
                        }
                        th.ip = 1;
                        return ((STable) th.lex0).mo.roleFactory.
                            Invoke(th, to_pass, null);
                    }
                case 1:
                    lock (th.lex0) {
                        ((STable) th.lex0).mo.instCache[(string) th.lex1]
                            = ((Variable) th.resultSlot).Fetch();
                    }
                    th.caller.resultSlot = th.resultSlot;
                    return th.Return();
                default:
                    return Die(th, "Invalid IP");
            }
        }
        public static Frame InstantiateRole(Frame th, Variable[] pcl) {
            return IRSI.Binder(th, null, null, pcl, null, false, null);
        }

        private static STable DoRoleApply(STable b,
                STable role) {
            STable n = new STable(b.name + " but " + role.name);
            if (role.mo.local_attr.Count != 0)
                throw new NieczaException("RoleApply with attributes NYI");
            if (role.mo.superclasses.Count != 0)
                throw new NieczaException("RoleApply with superclasses NYI");
            STable[] nmro = new STable[b.mo.mro.Length + 1];
            Array.Copy(b.mo.mro, 0, nmro, 1, b.mo.mro.Length);
            nmro[0] = n;
            n.FillClass(b.all_slot, new STable[] { b }, nmro);
            foreach (P6how.MethodInfo mi in role.mo.lmethods)
                n.mo.lmethods.Add(mi);
            foreach (P6how.AttrInfo ai in role.mo.local_attr)
                n.mo.local_attr.Add(ai);
            n.mo.Invalidate();

            n.how = BoxAny<STable>(n, b.how).Fetch();
            n.typeObject = n.initObject = new P6opaque(n);
            n.typeVar = n.initVar = NewROScalar(n.typeObject);
            ((P6opaque)n.typeObject).slots = null;

            return n;
        }

        public static STable RoleApply(STable b,
                STable role) {
            lock (b) {
                STable rs;
                if (b.mo.butCache.TryGetValue(role, out rs))
                    return rs;
                return b.mo.butCache[role] = DoRoleApply(b, role);
            }
        }

        public static Frame StartP6Thread(Frame th, P6any sub) {
            th.MarkSharedChain();
            Thread thr = new Thread(delegate () {
                    rlstack = new LastFrameNode();
                    rlstack.cur = th;
                    RunInferior(sub.Invoke(GetInferiorRoot(),
                            Variable.None, null));
                });
            thr.Start();
            th.resultSlot = thr;
            return th;
        }

        public static void RunMain(RuntimeUnit main_unit) {
            string trace = Environment.GetEnvironmentVariable("NIECZA_TRACE");
            if (trace != null) {
                if (trace == "all") {
                    TraceFlags = TRACE_CUR;
                    TraceFreq = 1;
                } else if (trace == "stat") {
                    TraceFlags = TRACE_ALL;
                    string p = Environment.GetEnvironmentVariable("NIECZA_TRACE_PERIOD");
                    if (!int.TryParse(p, out TraceFreq))
                        TraceFreq = 1000000;
                } else {
                    Console.Error.WriteLine("Unknown trace option {0}", trace);
                }
                TraceCount = TraceFreq;
            }
            try {
                // TODO: settings
                Kernel.RunInferior(Kernel.GetInferiorRoot().
                        MakeChild(null, main_unit.mainline, AnyP));
            } catch (NieczaException n) {
                Console.Error.WriteLine("Unhandled exception: {0}", n);
                Environment.Exit(1);
            }
        }

        class ExitRunloopException : Exception {
            public ResumeUnwindException payload;
            public ExitRunloopException(ResumeUnwindException p) { payload = p; }
        }
        public static SubInfo ExitRunloopSI =
            new SubInfo("ExitRunloop", ExitRunloopC);
        private static Frame ExitRunloopC(Frame th) {
            throw new ExitRunloopException(th.lex0 as ResumeUnwindException);
        }

        public const int TRACE_CUR = 1;
        public const int TRACE_ALL = 2;

        public static int TraceFreq;
        public static int TraceCount;
        public static int TraceFlags;

        private static void DoTrace(Frame cur) {
            TraceCount = TraceFreq;
            if ((TraceFlags & TRACE_CUR) != 0)
                Console.WriteLine("{0}|{1} @ {2}",
                        cur.DepthMark(), cur.info.name, cur.ip);
            if ((TraceFlags & TRACE_ALL) != 0) {
                Console.WriteLine("Context:" + DescribeBacktrace(cur, null));
            }
        }

        public static void RunCore(ref Frame cur) {
            // TimToady says that any exception thrown (but not internally
            // caught) during CATCH or LEAVE processing becomes the new
            // exception
            Exception handle = null;
            for(;;) {
                try {
                    if (handle is ResumeUnwindException) {
                        ResumeUnwindException rue =
                            (ResumeUnwindException) handle;
                        cur = Kernel.Unwind(cur, rue.type, rue.to_frame,
                                rue.to_ip, rue.to_data, null, null,
                                rue.p6backtrace);
                    }
                    else if (handle != null) {
                        if (Frame.AllExceptions)
                            Console.Error.WriteLine(handle.ToString());
                        cur = Kernel.Die(cur, handle.ToString());
                    }
                    handle = null;

                    if (TraceCount != 0) {
                        for(;;) {
                            if (--TraceCount == 0)
                                DoTrace(cur);
                            cur = cur.code(cur);
                        }
                    } else {
                        for(;;)
                            cur = cur.code(cur);
                    }
                } catch (ExitRunloopException ere) {
                    if (ere.payload != null)
                        throw ere.payload;
                    return;
                } catch (Exception ex) {
                    handle = ex;
                }
            }
        }

        // we like to make refs to these, so moving arrays is untenable
        class LastFrameNode {
            public LastFrameNode next, prev;
            public Frame cur, root;
        }
        [ThreadStatic] static LastFrameNode rlstack;
        public static void SetTopFrame(Frame f) {
            rlstack.cur = f;
        }

        // it is an error to throw an exception between GetInferiorRoot
        // and RunInferior
        public static Frame GetInferiorRoot() {
            LastFrameNode lfn = rlstack;
            if (lfn == null)
                lfn = rlstack = new LastFrameNode();
            if (lfn.next == null) {
                lfn.next = new LastFrameNode();
                lfn.next.prev = lfn;
            }
            Frame l = lfn.cur;
            rlstack = lfn.next;
            return lfn.next.cur = lfn.next.root = ((l == null ?
                        new Frame(null, null, ExitRunloopSI, Kernel.AnyP) :
                        l.MakeChild(null, ExitRunloopSI, AnyP)));
        }

        public static Variable RunInferior(Frame f) {
            LastFrameNode newlfn = rlstack;
            rlstack = newlfn;
            Variable result;

            try {
                Frame nroot = newlfn.root;
                newlfn.cur = f;
                RunCore(ref newlfn.cur);
                if (newlfn.cur != nroot) {
                    Console.Error.WriteLine("WRONG ExitRunloop TAKEN:" + DescribeBacktrace(newlfn.cur, null));
                    Console.Error.WriteLine("Correct:" + DescribeBacktrace(nroot, null));
                }
                result = (Variable) nroot.resultSlot;
            } finally {
                rlstack = newlfn.prev;
            }

            return result;
        }

        public static void AddCap(List<Variable> p,
                VarHash n, P6any cap) {
            Variable[] fp = cap.GetSlot("positionals") as Variable[];
            VarHash fn = cap.GetSlot("named")
                as VarHash;
            p.AddRange(fp);
            if (fn != null) AddMany(n, fn);
        }

        public static void AddMany(VarHash d1,
                VarHash d2) {
            foreach (KeyValuePair<string,Variable> kv in d2) {
                d1[kv.Key] = kv.Value;
            }
        }

        [ThreadStatic]
        public static Dictionary<string, StashEnt> currentGlobals;

        public static Variable GetGlobal(string key) {
            return currentGlobals[key].v;
        }

        public static void BindGlobal(string key, Variable to) {
            currentGlobals[key].v = to;
        }

        internal static void CreateBasicTypes() {
            CodeMO = new STable("Code"); // forward decl
            MuMO = new STable("Mu");
            Handler_Vonly(MuMO, "defined", new CtxBoolNativeDefined(),
                    new CtxRawNativeDefined());
            Handler_Vonly(MuMO, "Bool", new CtxBoolNativeDefined(),
                    new CtxRawNativeDefined());
            Handler_Vonly(MuMO, "item", new CtxReturnSelfItem(), null);

            MuMO.FillProtoClass(null, new string[] { });
            MuMO.Invalidate();

            AnyMO = new STable("Any");
            Handler_Vonly(AnyMO, "list", new CtxAnyList(), null);
            WrapIndexy(AnyMO, "postcircumfix:<[ ]>", new IxAnyAtPos(),
                    null, null, new IxAnyBindPos());
            WrapIndexy(AnyMO, "postcircumfix:<{ }>", new IxAnyAtKey(),
                    new IxAnyExistsKey(), new IxAnyDeleteKey(),
                    new IxAnyBindKey());
            AnyMO.FillProtoClass(MuMO, new string[] { });
            AnyMO.Invalidate();

            CodeMO.FillProtoClass(AnyMO, "outer", "info");
            SubInvokeSubSI.param = new object[] { null, new InvokeSub() };
            CodeMO.AddMethod(0, "postcircumfix:<( )>", MakeSub(SubInvokeSubSI, null));
            CodeMO.Invalidate();

            BlockMO = new STable("Block");
            RoutineMO = new STable("Routine");
            WhateverCodeMO = new STable("WhateverCode");
            SubMO = new STable("Sub");
            SubmethodMO = new STable("Submethod");
            MethodMO = new STable("Method");
            RegexMO = new STable("Regex");

            BlockMO.FillProtoClass(CodeMO, "outer", "info");
            RoutineMO.FillProtoClass(BlockMO, "outer", "info");
            WhateverCodeMO.FillProtoClass(BlockMO, "outer", "info");
            SubMO.FillProtoClass(RoutineMO, "outer", "info");
            MethodMO.FillProtoClass(RoutineMO, "outer", "info");
            SubmethodMO.FillProtoClass(RoutineMO, "outer", "info");
            RegexMO.FillProtoClass(MethodMO, "outer", "info");

            LabelMO = new STable("Label");
            LabelMO.FillProtoClass(AnyMO, "target", "name");

            // forward reference
            StrMO = new STable("Str");
            BoolMO = new STable("Bool");

            IntMO = new STable("Int");
            Handler_Vonly(IntMO, "Numeric", new CtxReturnSelf(),
                    new CtxCallMethodUnboxNumeric(null));
            Handler_PandBox(IntMO, "Bool", new CtxIntBool(), BoolMO);
            Handler_PandBox(IntMO, "Str", new CtxIntStr(), StrMO);
            Handler_PandCont(IntMO, "succ", new CtxIntSuccish(+1));
            Handler_PandCont(IntMO, "pred", new CtxIntSuccish(-1));
            IntMO.FillProtoClass(AnyMO);

            Handler_Vonly(BoolMO, "Bool", new CtxReturnSelf(),
                    new CtxBoolUnbox());
            BoolMO.FillProtoClass(IntMO, "index");
            TrueV  = NewROScalar(BoxRaw<int>(1, BoolMO));
            FalseV = NewROScalar(BoxRaw<int>(0, BoolMO));
            FalseV.Fetch().SetSlot("index", BoxAnyMO(0, IntMO));
            TrueV.Fetch().SetSlot("index", BoxAnyMO(1, IntMO));

            Handler_Vonly(StrMO, "Str", new CtxReturnSelf(),
                    new CtxJustUnbox<string>(""));
            Handler_PandBox(StrMO, "Bool", new CtxStrBool(), BoolMO);
            Handler_PandCont(StrMO, "succ", new CtxStrSuccish(true));
            Handler_PandCont(StrMO, "pred", new CtxStrSuccish(false));
            StrMO.FillProtoClass(AnyMO);

            JunctionMO = new STable("Junction");
            Handler_PandBox(JunctionMO, "Bool", new CtxJunctionBool(), BoolMO);
            JunctionMO.AddMethod(0, "FALLBACK", MakeSub(JunctionFallbackSI, null));
            JunctionMO.FillProtoClass(MuMO, "kind_", "eigenstates_");

            IteratorMO = new STable("Iterator");
            IteratorMO.FillProtoClass(AnyMO);

            NumMO = new STable("Num");
            Handler_Vonly(NumMO, "Numeric", new CtxReturnSelf(),
                    new CtxCallMethodUnboxNumeric(null));
            Handler_Vonly(NumMO, "Str", new CtxStrNativeNum2Str(),
                    new CtxRawNativeNum2Str());
            Handler_PandBox(NumMO, "Bool", new CtxNum2Bool(), BoolMO);
            Handler_PandCont(NumMO, "succ", new CtxNumSuccish(+1));
            Handler_PandCont(NumMO, "pred", new CtxNumSuccish(-1));
            NumMO.FillProtoClass(AnyMO);

            RatMO = new STable("Rat");
            Handler_Vonly(RatMO, "Numeric", new CtxReturnSelf(),
                    new CtxCallMethodUnboxNumeric(null));
            Handler_PandBox(RatMO, "Bool", new CtxRatBool(), BoolMO);
            Handler_PandBox(RatMO, "Str", new CtxRatStr(), StrMO);
            Handler_PandCont(RatMO, "succ", new CtxRatSuccish(true));
            Handler_PandCont(RatMO, "pred", new CtxRatSuccish(false));
            RatMO.FillProtoClass(AnyMO);

            FatRatMO = new STable("FatRat");
            Handler_Vonly(FatRatMO, "Numeric", new CtxReturnSelf(),
                    new CtxCallMethodUnboxNumeric(null));
            Handler_PandBox(FatRatMO, "Bool", new CtxFatRatBool(), BoolMO);
            Handler_PandBox(FatRatMO, "Str", new CtxFatRatStr(), StrMO);
            Handler_PandCont(FatRatMO, "succ", new CtxFatRatSuccish(true));
            Handler_PandCont(FatRatMO, "pred", new CtxFatRatSuccish(false));
            FatRatMO.FillProtoClass(AnyMO);

            ComplexMO = new STable("Complex");
            Handler_Vonly(ComplexMO, "Numeric", new CtxReturnSelf(),
                    new CtxCallMethodUnboxNumeric(null));
            Handler_PandBox(ComplexMO, "Bool", new CtxComplexBool(), BoolMO);
            Handler_PandBox(ComplexMO, "Str", new CtxComplexStr(), StrMO);
            Handler_PandCont(ComplexMO, "succ", new CtxComplexSuccish(+1));
            Handler_PandCont(ComplexMO, "pred", new CtxComplexSuccish(-1));
            ComplexMO.FillProtoClass(AnyMO);

            StashMO = new STable("Stash");
            StashMO.FillProtoClass(AnyMO);
            StashP = new P6opaque(StashMO);

            ClassHOWMO = new STable("ClassHOW");
            ClassHOWMO.FillProtoClass(AnyMO);

            ParcelMO = new STable("Parcel");
            Handler_PandBox(ParcelMO, "iterator", new CtxParcelIterator(),
                    IteratorMO);
            WrapHandler1(ParcelMO, "LISTSTORE", new IxParcelLISTSTORE());
            Handler_Vonly(ParcelMO, "list", new CtxParcelList(), null);
            ParcelMO.FillProtoClass(AnyMO);

            ListMO = new STable("List");
            WrapIndexy(ListMO, "postcircumfix:<[ ]>", new IxListAtPos(false),
                    null, null, new IxListBindPos());
            Handler_Vonly(ListMO, "pop", new PopList(), null);
            Handler_Vonly(ListMO, "shift", new ShiftList(), null);
            WrapPushy(ListMO, "push", new PushList());
            WrapPushy(ListMO, "unshift", new UnshiftList());
            Handler_PandBox(ListMO, "iterator", new CtxListIterator(),
                    IteratorMO);
            Handler_PandBox(ListMO, "Bool", new CtxListBool(), BoolMO);
            Handler_PandBoxInty(ListMO, "Numeric", new CtxListNum());
            Handler_Vonly(ListMO, "list", new CtxReturnSelfList(), null);
            ListMO.FillProtoClass(AnyMO, "items", "rest");

            ArrayMO = new STable("Array");
            WrapHandler1(ArrayMO, "LISTSTORE", new IxArrayLISTSTORE());
            WrapIndexy(ArrayMO, "postcircumfix:<[ ]>", new IxListAtPos(true),
                    null, null, new IxListBindPos());
            ArrayMO.FillProtoClass(ListMO, "items", "rest");

            HashMO = new STable("Hash");
            WrapHandler1(HashMO, "LISTSTORE", new IxHashLISTSTORE());
            WrapIndexy(HashMO, "postcircumfix:<{ }>", new IxHashAtKey(),
                    new IxHashExistsKey(), new IxHashDeleteKey(),
                    new IxHashBindKey());
            Handler_PandBox(HashMO, "iterator", new CtxHashIterator(), IteratorMO);
            Handler_PandBox(HashMO, "Bool", new CtxHashBool(), BoolMO);
            Handler_Vonly(HashMO, "hash", new CtxReturnSelfList(), null);
            HashMO.FillProtoClass(AnyMO);

            CursorMO = new STable("Cursor");
            WrapIndexy(CursorMO, "postcircumfix:<{ }>", new IxCursorAtKey(),
                    AnyMO.mro_exists_key, AnyMO.mro_delete_key,
                    AnyMO.mro_bind_key);
            WrapIndexy(CursorMO, "postcircumfix:<[ ]>", new IxCursorAtPos(),
                    null, null, AnyMO.mro_bind_pos);
            CursorMO.FillProtoClass(AnyMO);

            MatchMO = new STable("Match");
            WrapIndexy(MatchMO, "postcircumfix:<{ }>", new IxCursorAtKey(),
                    AnyMO.mro_exists_key, AnyMO.mro_delete_key,
                    AnyMO.mro_bind_key);
            WrapIndexy(MatchMO, "postcircumfix:<[ ]>", new IxCursorAtPos(),
                    null, null, AnyMO.mro_bind_pos);
            Handler_PandBox(MatchMO, "Str", new CtxMatchStr(), StrMO);
            MatchMO.FillProtoClass(AnyMO);

            ScalarMO = new STable("Scalar");
            ScalarMO.FillProtoClass(AnyMO);
        }

        // This is a library function in .NET 4
        public delegate string JoinSFormatter<T>(T x);
        public static string JoinS<T>(string sep, IEnumerable<T> things) {
            return JoinS(sep, things, delegate(T y) { return y.ToString(); });
        }
        public static string JoinS<T>(string sep, IEnumerable<T> things,
                JoinSFormatter<T> fmt) {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            bool fst = true;
            foreach (T x in things) {
                if (!fst) sb.Append(sep);
                fst = false;
                sb.Append(fmt(x));
            }
            return sb.ToString();
        }

        public static System.IO.TextReader OpenStdin() {
            return new System.IO.StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
        }

        public static System.IO.TextWriter OpenStdout() {
            return new System.IO.StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding);
        }

        public static System.IO.TextWriter OpenStderr() {
            return new System.IO.StreamWriter(Console.OpenStandardError(), Console.OutputEncoding);
        }

        public static Variable NewLabelVar(Frame fr, string name) {
            P6opaque dob = new P6opaque(LabelMO);
            fr.MarkSharedChain();
            dob.slots[0] = fr;
            dob.slots[1] = name;
            return NewROScalar(dob);
        }

        private static string DescribeException(int type, Frame tgt,
                string name, object payload) {
            if (type != SubInfo.ON_DIE)
                return "Illegal control operator: " +
                    SubInfo.DescribeControl(type, tgt, name);
            try {
                Variable v = (Variable) payload;
                return v.Fetch().mo.mro_raw_Str.Get(v);
            } catch (Exception ex) {
                return "(stringificiation failed: " + ex + ")";
            }
        }

        // exception processing goes in two stages
        // 1. find the correct place to unwind to, calling CATCH filters
        // 2. unwind, calling LEAVE functions
        public static Frame SearchForHandler(Frame th, int type, Frame tgt,
                int unused, string name, object payload) {
            Frame csr;

            Frame unf = null;
            int unip = 0;

            // make sure we have a list for RunCATCH
            // TODO: possibly should make X::AdHoc?
            if (type == SubInfo.ON_DIE) {
                payload = Kernel.NewRWListVar(Builtins.array_constructor(
                    (Variable)payload).Fetch());
            }

            for (csr = th; ; csr = csr.DynamicCaller()) {
                if (csr == null)
                    break; // unhandled exception, Unwind handles
                if (type == SubInfo.ON_NEXTDISPATCH) {
                    if (csr.curDisp != null) {
                        unf = csr;
                        break;
                    }
                    continue;
                }
                // for lexoticism
                if (tgt != null && tgt != csr)
                    continue;
                unip = csr.info.FindControlEnt(csr.ip, type, name);
                if (unip >= 0) {
                    if (csr.info == Builtins.RunCATCH_I && type == SubInfo.ON_DIE) {
                        // Fudgy implementation of SIMPLECATCH
                        // A non-control exception has been thrown from handler
                        // Unwind will DTRT with control flow, but we need
                        // to save the exception(s)
                        // avoids "trytles all the way down"
                        Variable unh = (Variable)csr.lex2;
                        Variable cur = (Variable)csr.lex3;
                        unh.Fetch().mo.mro_push.Invoke(unh, new Variable[] {
                                cur, (Variable) payload });
                    }
                    unf = csr;
                    break;
                }

                if (type == SubInfo.ON_DIE && csr.info.catch_ != null) {
                    Frame nfr = Builtins.RunCATCH_I.Binder(
                        Kernel.GetInferiorRoot(), null, null, null, null,
                        false, null);
                    nfr.lex0 = Kernel.MakeSub(csr.info.catch_, csr);
                    nfr.lex1 = payload;
                    Variable np = Kernel.RunInferior(nfr);
                    if (np.Fetch().mo.mro_raw_Bool.Get(np)) {
                        payload = np;
                    } else {
                        // leave old payload so Unwind can install it
                        unf = csr;
                        unip = -1;
                        break;
                    }
                }

                if (type != SubInfo.ON_DIE && csr.info.control != null) {
                    P6any sub = Kernel.MakeSub(csr.info.control, csr);
                    Frame nfr = csr.info.control.Binder(
                        Kernel.GetInferiorRoot(), csr, sub,
                        new Variable[] {
                            Builtins.MakeParcel(Builtins.MakeInt(type),
                                Kernel.BoxAnyMO(name, Kernel.StrMO),
                                tgt == null ? Kernel.AnyMO.typeVar :
                                    Kernel.NewROScalar(tgt))
                        }, null, false, null);
                    Variable np = Kernel.RunInferior(nfr);
                    if (np.Fetch().mo.mro_raw_Bool.Get(np)) {
                        unf = csr;
                        unip = -1;
                        break;
                    }
                }
            }

            if (unf == null && type == SubInfo.ON_WARNING) {
                Console.Error.WriteLine(name + DescribeBacktrace(th, null));
                return th;
            }

            try {
                return Unwind(th, type, unf, unip, payload, tgt, name, null);
            } catch (Exception e) {
                Panic(e.ToString());
                return null;
            }
        }

        public static string DescribeBacktrace(Frame from, Frame upto) {
            StringBuilder sb = new StringBuilder();
            while (from != upto) {
                sb.Append(Console.Out.NewLine);
                try {
                    sb.AppendFormat("  at {0} line {1} ({2} @ {3}) {4}",
                            new object[] {
                            from.ExecutingFile(), from.ExecutingLine(),
                            from.info.name, from.ip,
                            Frame.VerboseExceptions ? from.DescribeArgs() : ""
                        });
                } catch (Exception ex) {
                    sb.AppendFormat("  (frame display failed: {0})", ex);
                }
                from = from.DynamicCaller();
            }
            return sb.ToString();
        }

        // let's try to avoid looping failure
        static void Panic(string str) {
            Console.Error.WriteLine("Internal error in exception dispatch: " + str);
            Environment.Exit(1);
        }

        public static Frame Unwind(Frame th, int type, Frame tf, int tip,
                object td, Frame tgt, string name, string bt) {
            Frame csr = th;
            if (th.info == null)
                Panic("Catching frame has no info?");
            if (th.caller == null)
                Panic("Catching frame has no caller?" + th.info.name);
            while (csr != tf) {
                if (csr == null) Panic("Unwinding into null frame");
                if (csr.info == null) Panic("Null SubInfo?");
                if (csr.info == ExitRunloopSI) {
                    // when this exception reaches the outer runloop,
                    // more frames will be added
                    if (bt == null)
                        bt = DescribeException(type, tgt, name, td);
                    csr.lex0 = new ResumeUnwindException(type, tf, tip,
                            td, bt + DescribeBacktrace(th, csr.caller));
                    return csr;
                }
                if (csr.DynamicCaller() != csr.caller) {
                    csr = csr.DynamicCaller();
                } else {
                    if (csr.caller == null) Panic(csr.info.name + " has no caller?");
                    // TODO: catch generated exceptions and add to @!
                    csr.caller.resultSlot = Kernel.NilP == null ? null :
                        Kernel.NilP.mo.typeVar;
                    Kernel.SetTopFrame(csr);
                    csr = csr.Return();
                }
            }
            if (type == SubInfo.ON_NEXTDISPATCH) {
                if (tf.curDisp == null)
                    Panic("ON_NEXTDISPATCH caught by nondispatch frame?? " + csr.info.name);
                // These are a bit special because there isn't actually a
                // catching frame.
                DispatchEnt de = tf.curDisp.next;
                P6opaque o = td as P6opaque;
                if (de != null) {
                    Variable[] p = tf.pos;
                    VarHash n = tf.named;
                    if (o != null) {
                        p = (Variable[]) o.slots[0];
                        n = o.slots[1] as VarHash;
                    }
                    return de.info.Binder(tf.caller, de.outer, de.ip6, p, n,
                            false, de);
                } else {
                    tf.caller.resultSlot = AnyMO.typeVar;
                    return tf.caller;
                }
            } else if (type == SubInfo.ON_DIE) {
                Variable exn = (Variable)td; // will be an Array
                if (exn.Fetch().mo.mro_raw_Numeric.Get(exn) == 1)
                    exn = exn.Fetch().mo.mro_at_pos.Get(exn, Builtins.MakeInt(0));
                tf.LexicalBind("$!", (Variable)exn);
                td = AnyMO.typeVar;
            }
            tf.ip = tip;
            tf.resultSlot = td;
            if (tip < 0) {
                // catch IP of -1 means to force an immediate return, as
                // when a CATCH phaser is triggered.
                tf.caller.resultSlot = Kernel.NilP.mo.typeVar;
                return tf.Return();
            } else {
                return tf;
            }
        }

        public static void Main(string[] args) {
            string cmd = args.Length > 0 ? args[0] : "-help";

            if (cmd == "-run" && args.Length == 2) {
                RuntimeUnit ru = (RuntimeUnit)
                    RuntimeUnit.reg.LoadUnit(args[1]).root;

                ru.PrepareEval();
                RunMain(ru);
            } else {
                Console.WriteLine("usage: Kernel.dll -run Unit.Name");
            }
        }
    }

    public class Config {
        public static int CGVerbose =
            int.Parse(Environment.GetEnvironmentVariable("NIECZA_CODEGEN_TRACE") ?? "0");
        public static bool CGVerifiable =
            Environment.GetEnvironmentVariable("NIECZA_CODEGEN_UNVERIFIABLE") != null ? false : true;
        public static readonly bool C3Trace =
            Environment.GetEnvironmentVariable("NIECZA_C3_TRACE") != null;
    }
}
