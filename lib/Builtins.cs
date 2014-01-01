using Niecza;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using System.Net.Sockets;
using SpecialMathFunctions;

namespace Niecza {
    // IForeignInterpreter is used for runtime loading of the Perl 5
    // interop module (the CLR likes us to use interfaces for inter-module
    // communication like this).
    public interface IForeignInterpreter {
        Variable Eval(string code);
    }
    // Since Mono.Posix is not available on Windows, we want to access it
    // using late binding so that Niecza will at least load on Windows.
    // Of course actually trying to use the POSIX stuff on Windows will die.
    class PosixWrapper {
        static readonly Assembly Mono_Posix;
        static readonly Type Syscall, AccessModes, FilePermissions, Stat, Signum, Stdlib;

        // copied from Mono.Posix.dll; constant values are part of the
        // stable ABI so these can't change
        public const int R_OK = 1;
        public const int W_OK = 2;
        public const int X_OK = 4;
        public const int F_OK = 8;

        // references to methods that can be directly called
        public readonly static Func<uint> getuid, geteuid, getgid, getegid;
        public readonly static Func<uint,uint,int> setreuid, setregid;
        public readonly static Func<string,int> system, rmdir;

        // methods and fields that must be used through wrappers
        static readonly MethodInfo m_stat, m_access, m_raise, m_chmod;
        static readonly FieldInfo  f_dev, f_ino, f_mode, f_nlink, f_uid, f_gid,
            f_rdev, f_size, f_blksize, f_blocks, f_atime, f_mtime, f_ctime;

        // wrappers for methods that need to be wrapped, typically because
        // they take or return a data type defined in Mono.Posix
        public static int access(string pathname, int mode) {
            return (int) m_access.Invoke(null, new object[] {
                pathname, Enum.ToObject(AccessModes, mode) });
        }

        public static void raise(string sig) {
            m_raise.Invoke(null, new object[] { Enum.Parse(Signum, sig) });
        }

        public static long[] stat(string pathname) {
            object[] args = new object[] { pathname, null };
            long[] res = new long[14];
            res[0] = (int) m_stat.Invoke(null, args);

            res[1] =  (long)(ulong)f_dev.GetValue(args[1]);
            res[2] =  (long)(ulong)f_ino.GetValue(args[1]);
            res[3] =  ((IConvertible)f_mode.GetValue(args[1])).ToInt64(null);
            res[4] =  (long)(ulong)f_nlink.GetValue(args[1]);
            res[5] =  (uint)f_uid.GetValue(args[1]);
            res[6] =  (uint)f_gid.GetValue(args[1]);
            res[7] =  (long)(ulong)f_rdev.GetValue(args[1]);
            res[8] =  (long)f_size.GetValue(args[1]);
            res[9] =  (long)f_blksize.GetValue(args[1]);
            res[10] = (long)f_blocks.GetValue(args[1]);
            res[11] = (long)f_atime.GetValue(args[1]);
            res[12] = (long)f_mtime.GetValue(args[1]);
            res[13] = (long)f_ctime.GetValue(args[1]);

            return res;
        }

        public static int chmod(string pathname, int mode) {
            return (int) m_chmod.Invoke(null, new object[] {
                pathname, Enum.ToObject(FilePermissions, mode) });
        }

        static PosixWrapper() {
            Mono_Posix = Assembly.Load("Mono.Posix, Version=2.0.0.0, Culture=neutral, PublicKeyToken=0738eb9f132ed756");
            Syscall = Mono_Posix.GetType("Mono.Unix.Native.Syscall", true);
            AccessModes = Mono_Posix.GetType("Mono.Unix.Native.AccessModes", true);
            Stat = Mono_Posix.GetType("Mono.Unix.Native.Stat", true);
            FilePermissions = Mono_Posix.GetType("Mono.Unix.Native.FilePermissions", true);
            Signum = Mono_Posix.GetType("Mono.Unix.Native.Signum", true);
            Stdlib = Mono_Posix.GetType("Mono.Unix.Native.Stdlib", true);

            getuid = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>),
                    Syscall.GetMethod("getuid"));
            geteuid = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>),
                    Syscall.GetMethod("geteuid"));
            getgid = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>),
                    Syscall.GetMethod("getgid"));
            getegid = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>),
                    Syscall.GetMethod("getegid"));

            setreuid = (Func<uint,uint,int>)Delegate.CreateDelegate(
                typeof(Func<uint,uint,int>), Syscall.GetMethod("setreuid"));
            setregid = (Func<uint,uint,int>)Delegate.CreateDelegate(
                typeof(Func<uint,uint,int>), Syscall.GetMethod("setregid"));

            system = (Func<string,int>)Delegate.CreateDelegate(
                typeof(Func<string,int>), Stdlib.GetMethod("system"));
            rmdir = (Func<string,int>)Delegate.CreateDelegate(
                typeof(Func<string,int>), Syscall.GetMethod("rmdir"));

            m_stat = Syscall.GetMethod("stat");
            m_access = Syscall.GetMethod("access");
            m_raise = Stdlib.GetMethod("raise", new Type[] { Signum });
            m_chmod = Syscall.GetMethod("chmod");

            f_dev = Stat.GetField("st_dev");
            f_ino = Stat.GetField("st_ino");
            f_mode = Stat.GetField("st_mode");
            f_nlink = Stat.GetField("st_nlink");
            f_uid = Stat.GetField("st_uid");
            f_gid = Stat.GetField("st_gid");
            f_rdev = Stat.GetField("st_rdev");
            f_size = Stat.GetField("st_size");
            f_blksize = Stat.GetField("st_blksize");
            f_blocks = Stat.GetField("st_blocks");
            f_atime = Stat.GetField("st_atime");
            f_mtime = Stat.GetField("st_mtime");
            f_ctime = Stat.GetField("st_ctime");
        }
    }

    // A special variable type used to implement lvalue returns from substr.
    class SubstrLValue: Variable {
        Variable backing;
        int from;
        int length;

        private SubstrLValue() {}
        public SubstrLValue(Variable backing, int from, int length) {
            this.backing = backing;
            this.from = from;
            this.length = length;
        }

        public override P6any Fetch() {
            var mo = backing.Fetch().mo;
            string str = mo.mro_raw_Str.Get(backing);
            string sub = Builtins.LaxSubstring2(str, from, length);
            return sub == null ? mo.setting.StrMO.typeObj :
                Kernel.BoxRaw(sub, mo.setting.StrMO);
        }

        public override void Store(P6any v) {
            var mo = backing.Fetch().mo;
            string str = mo.mro_raw_Str.Get(backing);
            int left = (from < 0) ? 0 : (from > str.Length) ? str.Length : from;
            int right = ((length > (str.Length - left)) ? (str.Length - left) :
                (length < 0) ? 0 : length) + left;
            string lfr = str.Substring(0, left);
            string mfr = v.mo.mro_raw_Str.Get(v);
            string rfr = str.Substring(right);
            backing.Store(Kernel.BoxRaw<string>(lfr + mfr + rfr, mo.setting.StrMO));
        }

        public override void Freeze(Niecza.Serialization.FreezeBuffer fb) {
            fb.Byte((byte)Niecza.Serialization.SerializationCode.SubstrLValue);
            fb.ObjRef(backing);
            fb.Int(from);
            fb.Int(length);
        }
        internal static object Thaw(Niecza.Serialization.ThawBuffer tb) {
            var n = new SubstrLValue();
            tb.Register(n);
            n.backing = (Variable) tb.ObjRef();
            n.from = tb.Int();
            n.length = tb.Int();
            return n;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    class CpsAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    class ImplicitFrameAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    class ImplicitConstsAttribute : Attribute { }
}

// The special feature of this class is that Q:CgOp operations which are not
// recognized by the code generator are automatically turned into method calls
// against this class.
//
// We use the convention that lowercase methods are intended to be called from
// Perl 6 code while uppercase-starting methods are support routines.
public partial class Builtins {
    // When calling builtins, the binder is often bypassed, so we need to
    // check arguement types ourselves.  This is the really simple version
    // that doesn't handle junctions, not often used.
    public static P6any NominalCheck(string name, Variable v) {
        P6any r = v.Fetch();
        if (!r.mo.is_any)
            throw new NieczaException("Nominal type check failed for " + name +
                    " needed Any got " + r.mo.name);
        return r;
    }

    // Type-check val against any, tracking state appropriately for
    // junctions.
    static void CheckSpecialArg(Compartment s, int ix, ref int pivot,
            ref uint rank, P6any val) {
        if (val.mo.is_any) {
            // fine as is
        } else if (val.mo.HasType(s.JunctionMO)) {
            int jtype = Kernel.UnboxAny<int>((P6any)(val as P6opaque).slots[0]) / 2;
            if ((uint)jtype < rank) {
                rank = (uint)jtype;
                pivot = ix;
            }
        } else {
            throw new NieczaException("Nominal type check failed for #" + ix +
                    " needed Any got " + val.mo.name);
        }
    }

    // This function implements the actual looping part of autothreading
    internal static Variable AutoThread(Compartment c, P6any j,
            Func<Variable,Variable> dgt) {
        P6opaque j_ = (P6opaque)j;
        P6any listObj = (P6any) j_.slots[1];
        Variable[] list = Kernel.UnboxAny<Variable[]>(listObj);
        Variable[] nlist = new Variable[list.Length];
        for (int i = 0; i < list.Length; i++) {
            nlist[i] = dgt(list[i]);
        }
        P6any newList = Kernel.BoxRaw(nlist, c.ParcelMO);
        P6opaque newJunc = new P6opaque(c.JunctionMO);
        newJunc.slots[0] = j_.slots[0];
        newJunc.slots[1] = newList;
        return newJunc;
    }

    // These three functions implement type checking and junctional
    // autothreading for signatures like (Any, Any).
    //
    // You need to pass a reference to the function in so that these may
    // call it for autothreading.
    public static Variable HandleSpecial1(Constants c, Variable av0, P6any ao0,
            Func<Constants,Variable,Variable> dgt) {
        uint jrank = uint.MaxValue;
        int jpivot = -1;

        CheckSpecialArg(c.setting, 0, ref jpivot, ref jrank, ao0);

        if (jpivot < 0) return dgt(c, av0);

        return AutoThread(c.setting, ao0, (n) => dgt(c,n));
    }
    public static Variable HandleSpecial2(Constants c, Variable av0, Variable av1,
            P6any ao0, P6any ao1, Func<Constants,Variable,Variable,Variable> dgt) {
        uint jrank = uint.MaxValue;
        int jpivot = -1;

        CheckSpecialArg(c.setting, 0, ref jpivot, ref jrank, ao0);
        CheckSpecialArg(c.setting, 1, ref jpivot, ref jrank, ao1);

        if (jpivot < 0) return dgt(c, av0, av1);

        Variable[] avs = new Variable[] { av0, av1 };
        return AutoThread(c.setting, avs[jpivot].Fetch(), delegate(Variable n) {
            avs[jpivot] = n; return dgt(c, avs[0], avs[1]); });
    }
    public static Variable HandleSpecial3(Constants c, Variable av0, Variable av1,
            Variable av2, P6any ao0, P6any ao1, P6any ao2,
            Func<Constants,Variable,Variable,Variable,Variable> dgt) {
        uint jrank = uint.MaxValue;
        int jpivot = -1;
        var s = c.setting;

        CheckSpecialArg(s, 0, ref jpivot, ref jrank, ao0);
        CheckSpecialArg(s, 1, ref jpivot, ref jrank, ao1);
        CheckSpecialArg(s, 2, ref jpivot, ref jrank, ao2);

        if (jpivot < 0) return dgt(c, av0, av1, av2);

        Variable[] avs = new Variable[] { av0, av1, av2 };
        return AutoThread(s, avs[jpivot].Fetch(), delegate(Variable n) {
            avs[jpivot] = n; return dgt(c, avs[0], avs[1], avs[2]); });
    }

    // Truncating substrings useful in some places
    public static string LaxSubstring(string str, int from) {
        if (from < 0 || from > str.Length)
            return null;
        return str.Substring(from);
    }

    public static string LaxSubstring2(string str, int from, int l) {
        if (from < 0 || from > str.Length) return null;
        if (l >= str.Length - from) l = str.Length - from;
        if (l < 0) return null;
        return str.Substring(from, l);
    }

    // Here begins the Niecza numerics system.  Every CORE number is assigned
    // to one of these six groups.
    public const int NR_FIXINT  = 0;
    public const int NR_BIGINT  = 1;
    public const int NR_FIXRAT  = 2;
    public const int NR_FATRAT  = 3;
    public const int NR_FLOAT   = 4;
    public const int NR_COMPLEX = 5;

    // Coerce a value to numeric and return the group code
    public static P6any GetNumber(Variable v, P6any o, out int rank) {
        if (o.mo.num_rank >= 0) {
            rank = o.mo.num_rank;
        } else {
            if (o.Does(o.mo.setting.RealMO)) {
                rank = NR_FLOAT;
                o = InvokeMethod("Bridge", v).Fetch();
                return o;
            }
            o = o.mo.mro_Numeric.Get(v).Fetch();
            rank = o.mo.num_rank;
            if (rank < 0)
                throw new NieczaException("Not a valid primitive number " +
                        o.mo.name);
        }
        if (rank == NR_FIXINT && o is BoxObject<BigInteger>)
            rank = NR_BIGINT;
        return o;
    }

    // If a number is <= NR_COMPLEX, return it as if at NR_COMPLEX
    public static Complex PromoteToComplex(int rank, P6any vret) {
        Rat r; FatRat fr;
        if (!vret.IsDefined()) return new Complex(0,0);

        switch (rank) {
            case NR_FIXINT:
                return new Complex(Kernel.UnboxAny<int>(vret), 0);
            case NR_BIGINT:
                return new Complex((double)Kernel.UnboxAny<BigInteger>(vret), 0);
            case NR_FIXRAT:
                r = Kernel.UnboxAny<Rat>(vret);
                return new Complex((double)r.num / (double)r.den, 0);
            case NR_FATRAT:
                fr = Kernel.UnboxAny<FatRat>(vret);
                return new Complex((double)fr.num / (double)fr.den, 0);
            case NR_FLOAT:
                return new Complex(Kernel.UnboxAny<double>(vret), 0);
            case NR_COMPLEX:
            default:
                return Kernel.UnboxAny<Complex>(vret);
        }
    }

    // If a number is <= NR_FLOAT, return it as if at NR_FLOAT
    public static double PromoteToFloat(int rank, P6any vret) {
        Rat r; FatRat fr;
        if (!vret.IsDefined()) return 0;

        switch (rank) {
            case NR_FIXINT:
                return Kernel.UnboxAny<int>(vret);
            case NR_BIGINT:
                return (double)Kernel.UnboxAny<BigInteger>(vret);
            case NR_FIXRAT:
                r = Kernel.UnboxAny<Rat>(vret);
                return (double)r.num / (double)r.den;
            case NR_FATRAT:
                fr = Kernel.UnboxAny<FatRat>(vret);
                return (double)fr.num / (double)fr.den;
            case NR_FLOAT:
            default:
                return Kernel.UnboxAny<double>(vret);
        }
    }

    // If a number is <= NR_FATRAT, return it as if at NR_FATRAT
    public static FatRat PromoteToFatRat(int rank, P6any vret) {
        Rat r;
        if (!vret.IsDefined()) return new FatRat(BigInteger.Zero,BigInteger.One);

        switch (rank) {
            case NR_FIXINT:
                return new FatRat(Kernel.UnboxAny<int>(vret), BigInteger.One);
            case NR_BIGINT:
                return new FatRat(Kernel.UnboxAny<BigInteger>(vret), BigInteger.One);
            case NR_FIXRAT:
                r = Kernel.UnboxAny<Rat>(vret);
                return new FatRat(r.num, r.den);
            case NR_FATRAT:
            default:
                return Kernel.UnboxAny<FatRat>(vret);
        }
    }

    // If a number is <= NR_FIXRAT, return it as if at NR_FIXRAT
    public static Rat PromoteToFixRat(int rank, P6any vret) {
        if (!vret.IsDefined()) return new Rat(BigInteger.Zero, 1);

        switch (rank) {
            case NR_FIXINT:
                return new Rat(Kernel.UnboxAny<int>(vret), 1);
            case NR_BIGINT:
                return new Rat(Kernel.UnboxAny<BigInteger>(vret), 1);
            case NR_FIXRAT:
            default:
                return Kernel.UnboxAny<Rat>(vret);
        }
    }

    // If a number is <= NR_BIGINT, return it as if at NR_BIGINT
    public static BigInteger PromoteToBigInt(int rank, P6any vret) {
        if (!vret.IsDefined()) return BigInteger.Zero;

        switch (rank) {
            case NR_FIXINT:
                return Kernel.UnboxAny<int>(vret);
            case NR_BIGINT:
            default:
                return Kernel.UnboxAny<BigInteger>(vret);
        }
    }

    // If a number is <= NR_FIXINT, return it as if at NR_FIXINT
    public static int PromoteToFixInt(int rank, P6any vret) {
        if (!vret.IsDefined()) return 0;
        return Kernel.UnboxAny<int>(vret);
    }

    // Coerce a number to a real rational value - note that this loses
    // the "inexact" annotation carried by Nums
    public static void GetAsRational(Variable v,
            out BigInteger num, out BigInteger den) {
        int rk;
        P6any n = GetNumber(v, v.Fetch(), out rk);

        if (rk == NR_COMPLEX || rk == NR_FLOAT) {
            double dbl = 0;
            if (rk == NR_COMPLEX) {
                Complex c = Kernel.UnboxAny<Complex>(n);
                if (c.im != 0)
                    throw new NieczaException("Complex cannot be used here");
                dbl = c.re;
            } else {
                dbl = Kernel.UnboxAny<double>(n);
            }
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(dbl);
            num = (bits & ((1UL << 52) - 1)) + (1UL << 52);
            den = (1UL << 52);
            if ((bits & (1UL << 63)) != 0) num = -num;
            int power = ((int)((bits >> 52) & 0x7FF)) - 0x3FF;
            if (power > 0) num <<= power;
            else den <<= -power;
            SimplifyFrac(ref num, ref den);
        }
        else if (rk == NR_FATRAT) {
            FatRat r = Kernel.UnboxAny<FatRat>(n);
            num = r.num; den = r.den;
        }
        else if (rk == NR_FIXRAT) {
            Rat r = Kernel.UnboxAny<Rat>(n);
            num = r.num; den = r.den;
        }
        else if (rk == NR_BIGINT) {
            num = Kernel.UnboxAny<BigInteger>(n); den = BigInteger.One;
        }
        else {
            num = Kernel.UnboxAny<int>(n); den = BigInteger.One;
        }
    }

    // Coerce a real number to an integer, truncating towards 0
    public static bool GetAsInteger(Variable v, out int small,
            out BigInteger big) {
        int rk;
        P6any n = GetNumber(v, v.Fetch(), out rk);
        small = 0;

        if (rk == NR_COMPLEX || rk == NR_FLOAT) {
            double dbl = 0;
            if (rk == NR_COMPLEX) {
                Complex c = Kernel.UnboxAny<Complex>(n);
                if (c.im != 0)
                    throw new NieczaException("Complex cannot be used here");
                dbl = c.re;
            } else {
                dbl = Kernel.UnboxAny<double>(n);
            }
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(dbl);
            big = (bits & ((1UL << 52) - 1)) + (1UL << 52);
            int power = ((int)((bits >> 52) & 0x7FF)) - 0x433;
            if (power > 0) big <<= power;
            else big >>= -power;
            if ((bits & (1UL << 63)) != 0) big = -big;
        }
        else if (rk == NR_FATRAT) {
            FatRat r = Kernel.UnboxAny<FatRat>(n);
            big = r.num / r.den;
        }
        else if (rk == NR_FIXRAT) {
            Rat r = Kernel.UnboxAny<Rat>(n);
            big = r.num / r.den;
        }
        else if (rk == NR_BIGINT) {
            big = Kernel.UnboxAny<BigInteger>(n);
        }
        else {
            big = BigInteger.Zero; small = Kernel.UnboxAny<int>(n); return false;
        }
        return true;
    }

    public static void SimplifyFrac(ref BigInteger num, ref BigInteger den) {
        if (den.Sign < 0) {
            den = -den;
            num = -num;
        }
        if (num.Sign == 0) {
            den = BigInteger.One;
        }
        if (num.Sign != 0 && den.Sign != 0) {
            BigInteger g = BigInteger.GreatestCommonDivisor(num, den);
            if (g != BigInteger.One) {
                num /= g;
                den /= g;
            }
        }
    }

    public static double RatToFloat(BigInteger num, BigInteger den) {
        // TODO: avoid overflow
        return (double) num / (double) den;
    }

    public static Variable InvokeSub(P6any obj, params Variable[] pos) {
        return Kernel.RunInferior(obj.Invoke(Kernel.GetInferiorRoot(obj.mo.setting), pos, null));
    }

    public static Variable InvokeSub(P6any obj, Variable[] pos, VarHash nam) {
        return Kernel.RunInferior(obj.Invoke(Kernel.GetInferiorRoot(obj.mo.setting), pos, nam));
    }

    public static Variable InvokeMethod(string name, params Variable[] pos) {
        var o = pos[0].Fetch();
        return Kernel.RunInferior(o.InvokeMethod(
            Kernel.GetInferiorRoot(o.mo.setting), name, pos, null));
    }

    public static Variable InvokeMethod(string name, Variable[] pos, VarHash nam) {
        var o = pos[0].Fetch();
        return Kernel.RunInferior(o.InvokeMethod(
            Kernel.GetInferiorRoot(o.mo.setting), name, pos, nam));
    }

    public static bool ToBool(Variable v) {
        return v.Fetch().mo.mro_raw_Bool.Get(v);
    }
    public static string ToStr(Variable v) {
        return v.Fetch().mo.mro_raw_Str.Get(v);
    }
    public static double ToNum(Variable v) {
        return v.Fetch().mo.mro_raw_Numeric.Get(v);
    }

    // Most of the following functions get used for inline calls, so they
    // must use HandleSpecialX
    static readonly Func<Constants,Variable,Variable,Variable> numeq_d = numeq;
    [ImplicitConsts] public static Variable numeq(Constants c, Variable v1, Variable v2) {
        return numcompare(c, v1, v2, O_IS_EQUAL | O_COMPLEX_OK, numeq_d);
    }

    [ImplicitConsts] public static Variable numne(Constants c, Variable v1, Variable v2) {
        // NOTE that junctionalization uses == !  See check in numcompare
        return numcompare(c, v1, v2, O_IS_LESS | O_IS_GREATER | O_IS_UNORD |
                O_COMPLEX_OK, numeq_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> numlt_d = numlt;
    [ImplicitConsts] public static Variable numlt(Constants c, Variable v1, Variable v2) {
        return numcompare(c, v1, v2, O_IS_LESS, numlt_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> numle_d = numle;
    [ImplicitConsts] public static Variable numle(Constants c, Variable v1, Variable v2) {
        return numcompare(c, v1, v2, O_IS_EQUAL | O_IS_LESS, numle_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> numgt_d = numgt;
    [ImplicitConsts] public static Variable numgt(Constants c, Variable v1, Variable v2) {
        return numcompare(c, v1, v2, O_IS_GREATER, numgt_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> numge_d = numge;
    [ImplicitConsts] public static Variable numge(Constants c, Variable v1, Variable v2) {
        return numcompare(c, v1, v2, O_IS_GREATER | O_IS_EQUAL, numge_d);
    }

    internal static Variable strcompare(Constants c, Variable v1, Variable v2,
            int mask, Func<Constants,Variable,Variable,Variable> d) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        var s = c.setting;
        if (!(o1.mo.is_any && o2.mo.is_any)) {
            Variable jr = HandleSpecial2(c, v1, v2, o1, o2, d);
            // treat $x != $y as !($x == $y)
            if (mask == (O_IS_GREATER | O_IS_LESS | O_IS_UNORD))
                return jr.Fetch().mo.mro_raw_Bool.Get(jr) ? s.FalseV : s.TrueV;
            return jr;
        }
        int diff = string.CompareOrdinal(o1.mo.mro_raw_Str.Get(v1),
                o2.mo.mro_raw_Str.Get(v2));
        int mcom = (diff < 0) ? O_IS_LESS : (diff > 0) ? O_IS_GREATER :
            O_IS_EQUAL;
        return ((mask & mcom) != 0) ? s.TrueV : s.FalseV;
    }

    static readonly Func<Constants,Variable,Variable,Variable> streq_d = streq;
    [ImplicitConsts] public static Variable streq(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_EQUAL, streq_d);
    }

    [ImplicitConsts] public static Variable strne(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_LESS | O_IS_GREATER | O_IS_UNORD, streq_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> strlt_d = strlt;
    [ImplicitConsts] public static Variable strlt(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_LESS, strlt_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> strle_d = strle;
    [ImplicitConsts] public static Variable strle(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_EQUAL | O_IS_LESS, strle_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> strgt_d = strgt;
    [ImplicitConsts] public static Variable strgt(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_GREATER, strgt_d);
    }

    static readonly Func<Constants,Variable,Variable,Variable> strge_d = strge;
    [ImplicitConsts] public static Variable strge(Constants c, Variable v1, Variable v2) {
        return strcompare(c, v1, v2, O_IS_GREATER | O_IS_EQUAL, strge_d);
    }

    private static int substr_pos(Compartment s, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(), o2 = v2.Fetch();
        int r2;
        if (o2.Does(s.CodeMO)) {
            string s1 = o1.mo.mro_raw_Str.Get(v1);
            Variable no2 = InvokeSub(o2, s.MakeInt(s1.Length));
            r2 = (int)no2.Fetch().mo.mro_raw_Numeric.Get(no2);
        } else {
            r2 = (int)o2.mo.mro_raw_Numeric.Get(v2);
        }
        return r2;
    }

    private static int substr_len(Compartment s, Variable v1, int pos, Variable v3) {
        P6any o1 = v1.Fetch(), o3 = v3.Fetch();
        int r3;
        if (o3.Does(s.CodeMO)) {
            string s1 = o1.mo.mro_raw_Str.Get(v1);
            Variable no3 = InvokeSub(o3, s.MakeInt(s1.Length));
            r3 = (int)no3.Fetch().mo.mro_raw_Numeric.Get(no3) - pos;
        } else {
            r3 = (int)o3.mo.mro_raw_Numeric.Get(v3);
        }
        return r3;
    }

    static readonly Func<Constants,Variable,Variable,Variable,Variable> substr3_d = substr3;
    [ImplicitConsts] public static Variable substr3(Constants c, Variable v1, Variable v2, Variable v3) {
        P6any o1 = v1.Fetch(), o2 = v2.Fetch(), o3 = v3.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any && o3.mo.is_any))
            return HandleSpecial3(c, v1,v2,v3, o1,o2,o3, substr3_d);

        int pos = substr_pos(c.setting, v1, v2);
        int len = substr_len(c.setting, v1, pos, v3);
        return new SubstrLValue(v1, pos, len);
    }

    static readonly Func<Constants,Variable,Variable,Variable,Variable> substr_ro3_d = substr_ro3;
    [ImplicitConsts] public static Variable substr_ro3(Constants c, Variable v1, Variable v2, Variable v3) {
        var s = c.setting;
        P6any o1 = v1.Fetch(), o2 = v2.Fetch(), o3 = v3.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any && o3.mo.is_any))
            return HandleSpecial3(c, v1,v2,v3, o1,o2,o3, substr_ro3_d);

        int pos = substr_pos(s, v1, v2);
        int len = substr_len(s, v1, pos, v3);
        string str = v1.Fetch().mo.mro_raw_Str.Get(v1);
        string sub = Builtins.LaxSubstring2(str, pos, len);
        return sub == null ? s.StrMO.typeObj : s.MakeStr(sub);
    }

    static readonly Func<Constants,Variable,Variable,Variable> plus_d = plus;
    [ImplicitConsts] public static Variable plus(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1, a2, o1, o2, plus_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        var s = c.setting;

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            Complex v2 = PromoteToComplex(r2, n2);
            return s.MakeComplex(v1.re + v2.re, v1.im + v2.im);
        }
        if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            return s.MakeFloat(PromoteToFloat(r1, n1) + PromoteToFloat(r2, n2));
        }
        if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            return s.MakeFatRat(v1.num*v2.den + v2.num*v1.den, v1.den*v2.den);
        }
        if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            return s.MakeFixRat(v1.num*v2.den + v2.num*v1.den, ((BigInteger)v1.den)*v2.den);
        }
        if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            return s.MakeInt(PromoteToBigInt(r1, n1) + PromoteToBigInt(r2, n2));
        }
        return s.MakeInt((long)PromoteToFixInt(r1, n1) +
                (long)PromoteToFixInt(r2, n2));
    }

    static readonly Func<Constants,Variable,Variable,Variable> minus_d = minus;
    [ImplicitConsts] public static Variable minus(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1,a2, o1,o2, minus_d);

        var s = c.setting;
        if (s.InstantMO != null && o1.Isa(s.InstantMO) && o2.Isa(s.InstantMO)) {
            a1 = (Variable) o1.GetSlot(s.InstantMO, "$!val");
            a2 = (Variable) o2.GetSlot(s.InstantMO, "$!val");
            o1 = a1.Fetch();
            o2 = a2.Fetch();
        }

        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            Complex v2 = PromoteToComplex(r2, n2);
            return s.MakeComplex(v1.re - v2.re, v1.im - v2.im);
        }
        if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            return s.MakeFloat(PromoteToFloat(r1, n1) - PromoteToFloat(r2, n2));
        }
        if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            return s.MakeFatRat(v1.num*v2.den - v2.num*v1.den, v1.den*v2.den);
        }
        if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            return s.MakeFixRat(v1.num*v2.den - v2.num*v1.den, ((BigInteger)v1.den)*v2.den);
        }
        if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            return s.MakeInt(PromoteToBigInt(r1, n1) - PromoteToBigInt(r2, n2));
        }
        return s.MakeInt((long)PromoteToFixInt(r1, n1) -
                (long)PromoteToFixInt(r2, n2));
    }

    // Untested and probably unusable in the near term
    public static BigInteger big_pow(BigInteger v1, BigInteger v2) {
        if (v2 == BigInteger.Zero)
            return BigInteger.One;
        if (v1 == BigInteger.One)
            return BigInteger.One;
        if (v1 == BigInteger.Zero)
            return BigInteger.Zero;
        if (v1 == -BigInteger.One)
            return v2 % 2 == 0 ? BigInteger.One : -BigInteger.One;

        int CHUNK = 2000000000;
        int margin = (int) (v2 % (BigInteger) CHUNK);
        BigInteger number_chunks = (v2 - margin) / CHUNK;
        BigInteger margin_pow = BigInteger.Pow(v1, margin);
        BigInteger result = margin_pow;
        if (number_chunks > 0) {
            BigInteger chunk_pow = margin_pow * BigInteger.Pow(v1, CHUNK - margin);
            for (BigInteger i = 0; i < number_chunks; i++) {
                result *= chunk_pow;
            }
        }
        return result;
    }

    // Unifies the MakeFixRat logic and tries to avoid a huge computation
    // that will just be rounded away.
    // Invariant: pow >= 0
    static Variable RatPow(Compartment s, BigInteger num, BigInteger den, BigInteger pow) {
        if (den == -BigInteger.One) { den = BigInteger.One; num = -num; }

        if (den == BigInteger.One) {
            // den won't be getting any bigger
            return s.MakeFixRat(big_pow(num, pow), 1);
        }
        // den >= 2
        if (pow >= 64) {
            // Overflow is inevitable.
            return s.MakeFloat(Math.Pow(RatToFloat(num, den), (double)pow));
        }
        // we might consider detecting smaller overflows, but the penalty
        // of $int ** 63 is not _that_ huge.

        return s.MakeFixRat(big_pow(num, pow), big_pow(den, pow));
    }

    static readonly Func<Constants,Variable,Variable,Variable> pow_d = pow;
    [ImplicitConsts] public static Variable pow(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1, a2, o1, o2, pow_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        var s = c.setting;

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            Complex c1 = PromoteToComplex(r1, n1);
            Complex c2 = PromoteToComplex(r2, n2);
            double r = Math.Sqrt(c1.re * c1.re + c1.im * c1.im);
            if (r == 0.0) {
                return s.MakeComplex(0.0, 0.0);
            }
            double theta = Math.Atan2(c1.im, c1.re);
            // Log z = ln r + iθ
            // ($a.log * $b).exp;
            double lp_re = Math.Log(r)*c2.re - theta*c2.im;
            double lp_im = theta*c2.re + Math.Log(r)*c2.im;
            return s.MakeComplex(Math.Exp(lp_re) * Math.Cos(lp_im),
                               Math.Exp(lp_re) * Math.Sin(lp_im));
        }

        if (r1 == NR_FLOAT || (r2 != NR_BIGINT && r2 != NR_FIXINT)) {
            return s.MakeFloat(Math.Pow(PromoteToFloat(r1, n1), PromoteToFloat(r2, n2)));
        }

        BigInteger v2 = PromoteToBigInt(r2, n2);
        if (v2 >= 0) {
            if (r1 == NR_FIXINT || r1 == NR_BIGINT) {
                return s.MakeInt(big_pow(PromoteToBigInt(r1, n1), v2));
            }
            if (r1 == NR_FATRAT) {
                FatRat v1 = PromoteToFatRat(r1, n1);
                return s.MakeFatRat(big_pow(v1.num, v2), big_pow(v1.den, v2));
            }
            if (r1 == NR_FIXRAT) {
                Rat v1 = PromoteToFixRat(r1, n1);
                return RatPow(s, v1.num, v1.den, v2);
            }
        } else {
            if (r1 == NR_FIXINT || r1 == NR_BIGINT) {
                return RatPow(s, 1, PromoteToBigInt(r1, n1), -v2);
            }
            if (r1 == NR_FATRAT) {
                FatRat v1 = PromoteToFatRat(r1, n1);
                return s.MakeFatRat(big_pow(v1.den, -v2), big_pow(v1.num, -v2));
            }
            if (r1 == NR_FIXRAT) {
                Rat v1 = PromoteToFixRat(r1, n1);
                return RatPow(s, v1.den, v1.num, -v2);
            }
        }

        return s.MakeFloat(Math.Pow(PromoteToFloat(r1, n1), PromoteToFloat(r2, n2)));
    }

    static readonly Func<Constants,Variable,Variable> negate_d = negate;
    [ImplicitConsts] public static Variable negate(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, negate_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        var s = c.setting;

        if (r1 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            return s.MakeComplex(-v1.re, -v1.im);
        }
        if (r1 == NR_FLOAT) {
            return s.MakeFloat(-PromoteToFloat(r1, n1));
        }
        if (r1 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            return s.MakeFatRat(-v1.num, v1.den);
        }
        if (r1 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            return s.MakeFixRat(-v1.num, v1.den);
        }
        if (r1 == NR_BIGINT) {
            return s.MakeInt(-PromoteToBigInt(r1, n1));
        }
        return s.MakeInt(-(long)PromoteToFixInt(r1, n1));
    }

    static readonly Func<Constants,Variable,Variable> abs_d = abs;
    [ImplicitConsts] public static Variable abs(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, abs_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        var s = c.setting;

        if (r1 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            return s.MakeFloat(Math.Sqrt(v1.re * v1.re + v1.im * v1.im));
        }
        if (r1 == NR_FLOAT) {
            double v1 = PromoteToFloat(r1, n1);
            return s.MakeFloat(v1 < 0 ? -v1 : v1);
        }
        if (r1 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            return v1.num < 0 ? s.MakeFatRat(-v1.num, v1.den) : s.MakeFatRat(v1.num, v1.den);
        }
        if (r1 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            return v1.num < 0 ? s.MakeFixRat(-v1.num, v1.den) : s.MakeFixRat(v1.num, v1.den);
        }
        if (r1 == NR_BIGINT) {
            BigInteger v1 = PromoteToBigInt(r1, n1);
            return s.MakeInt(v1 < 0 ? -v1 : v1);
        }
        {
            long v1 = PromoteToFixInt(r1, n1);
            return s.MakeInt(v1 < 0 ? -v1 : v1);
        }
    }

    static readonly Func<Constants,Variable,Variable> ln_d = ln;
    [ImplicitConsts] public static Variable ln(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, ln_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        var s = c.setting;

        if (r1 == NR_COMPLEX) {
            // Log z = ln r + iθ
            Complex v1 = PromoteToComplex(r1, n1);
            return s.MakeComplex(Math.Log(Math.Sqrt(v1.re * v1.re + v1.im * v1.im)),
                               Math.Atan2(v1.im, v1.re));
        }
        if (r1 == NR_BIGINT) {
            BigInteger v1 = PromoteToBigInt(r1, n1);
            return s.MakeFloat(BigInteger.Log(v1));
        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return s.MakeFloat(Math.Log(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> exp_d = exp;
    [ImplicitConsts] public static Variable exp(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, exp_d);
        P6any n1 = GetNumber(a1, o1, out r1);

        if (r1 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            return c.setting.MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
                               Math.Exp(v1.re) * Math.Sin(v1.im));
        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(Math.Exp(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> gamma_d = gamma;
    [ImplicitConsts] public static Variable gamma(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, gamma_d);
        P6any n1 = GetNumber(a1, o1, out r1);

//        if (r1 == NR_COMPLEX) {
//            Complex v1 = PromoteToComplex(r1, n1);
//            return MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
//                               Math.Exp(v1.re) * Math.Sin(v1.im));
//        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(SpecialMathFunctions.SpecialFunctions.Gamma(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> lgamma_d = lgamma;
    [ImplicitConsts] public static Variable lgamma(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, lgamma_d);
        P6any n1 = GetNumber(a1, o1, out r1);

//        if (r1 == NR_COMPLEX) {
//            Complex v1 = PromoteToComplex(r1, n1);
//            return MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
//                               Math.Exp(v1.re) * Math.Sin(v1.im));
//        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(SpecialMathFunctions.SpecialFunctions.LogGamma(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> expm1_d = expm1;
    [ImplicitConsts] public static Variable expm1(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, expm1_d);
        P6any n1 = GetNumber(a1, o1, out r1);

//        if (r1 == NR_COMPLEX) {
//            Complex v1 = PromoteToComplex(r1, n1);
//            return MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
//                               Math.Exp(v1.re) * Math.Sin(v1.im));
//        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(SpecialMathFunctions.SpecialFunctions.ExpMinusOne(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> log1p_d = log1p;
    [ImplicitConsts] public static Variable log1p(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, log1p_d);
        P6any n1 = GetNumber(a1, o1, out r1);

//        if (r1 == NR_COMPLEX) {
//            Complex v1 = PromoteToComplex(r1, n1);
//            return MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
//                               Math.Exp(v1.re) * Math.Sin(v1.im));
//        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(SpecialMathFunctions.SpecialFunctions.LogOnePlusX(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable> erf_d = erf;
    [ImplicitConsts] public static Variable erf(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, erf_d);
        P6any n1 = GetNumber(a1, o1, out r1);

//        if (r1 == NR_COMPLEX) {
//            Complex v1 = PromoteToComplex(r1, n1);
//            return MakeComplex(Math.Exp(v1.re) * Math.Cos(v1.im),
//                               Math.Exp(v1.re) * Math.Sin(v1.im));
//        }
        {
            double v1 = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(SpecialMathFunctions.SpecialFunctions.Erf(v1));
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> atan2_d = atan2;
    [ImplicitConsts] public static Variable atan2(Constants c, Variable a1, Variable a2) {
        P6any o1 = a1.Fetch();
        P6any o2 = a2.Fetch();
        if (!o1.mo.is_any || !o2.mo.is_any)
            return HandleSpecial2(c, a1,a2,o1,o2, atan2_d);
        int r1;
        P6any n1 = GetNumber(a1, o1, out r1);
        int r2;
        P6any n2 = GetNumber(a2, o2, out r2);

        {
            double v1 = PromoteToFloat(r1, n1);
            double v2 = PromoteToFloat(r2, n2);
            return c.setting.MakeFloat(Math.Atan2(v1, v2));
        }
    }

    static readonly Func<Constants,Variable,Variable> floor_d = floor;
    [ImplicitConsts] public static Variable floor(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, floor_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        var s = c.setting;

        if (r1 == NR_COMPLEX) {
            throw new NieczaException("floor is only defined for Reals, you have a Complex()");
        }
        if (r1 == NR_FLOAT) {
            double v1 = PromoteToFloat(r1, n1);
            if (Double.IsNaN(v1) || Double.IsNegativeInfinity(v1) || Double.IsPositiveInfinity(v1)) {
                return s.MakeFloat(v1);
            }
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v1);
            BigInteger big = (bits & ((1UL << 52) - 1)) + (1UL << 52);
            int power = ((int)((bits >> 52) & 0x7FF)) - 0x433;
            // note: >>= has flooring semantics for signed values
            if ((bits & (1UL << 63)) != 0) big = -big;
            if (power > 0) big <<= power;
            else big >>= -power;
            return s.MakeInt(big);
        }
        if (r1 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            BigInteger rem;
            BigInteger red = BigInteger.DivRem(v1.num, v1.den, out rem);
            if (rem.Sign != 0 && v1.num.Sign < 0)
                return s.MakeInt(red - 1);
            else
                return s.MakeInt(red);
        }
        if (r1 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            BigInteger rem;
            BigInteger red = BigInteger.DivRem(v1.num, v1.den, out rem);
            if (rem.Sign != 0 && v1.num.Sign < 0)
                return s.MakeInt(red - 1);
            else
                return s.MakeInt(red);
        }
        return n1;
    }

    static readonly Func<Constants,Variable,Variable,Variable> gcd_d = gcd;
    [ImplicitConsts] public static Variable gcd(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1,a2, o1,o2, gcd_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);

        // SHOULD: optimize for the case of two small sized Ints
        return c.setting.MakeInt(BigInteger.GreatestCommonDivisor(PromoteToBigInt(r1, n1), PromoteToBigInt(r2, n2)));
    }

    static readonly Func<Constants,Variable,Variable,Variable,Variable> expmod_d = expmod;
    [ImplicitConsts] public static Variable expmod(Constants c, Variable a1, Variable a2, Variable a3) {
        P6any o1 = a1.Fetch();
        P6any o2 = a2.Fetch();
        P6any o3 = a3.Fetch();
        if (!o1.mo.is_any || !o2.mo.is_any || !o3.mo.is_any)
            return HandleSpecial3(c, a1,a2,a3,o1,o2,o3, expmod_d);
        int r1, r2, r3;
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        P6any n3 = GetNumber(a3, o3, out r3);

        return c.setting.MakeInt(BigInteger.ModPow(PromoteToBigInt(r1, n1),
                                         PromoteToBigInt(r2, n2),
                                         PromoteToBigInt(r3, n3)));
    }

    static readonly Func<Constants,Variable,Variable> lsb_d = lsb;
    [ImplicitConsts] public static Variable lsb(Constants c, Variable a1) {
        int r1;
        P6any o1 = a1.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1, o1, lsb_d);
        P6any n1 = GetNumber(a1, o1, out r1);

        return c.setting.MakeInt(PromoteToBigInt(r1, n1).Least_Significant_Bit_Position());
    }

    static readonly Func<Constants,Variable,Variable> msb_d = msb;
    [ImplicitConsts] public static Variable msb(Constants c, Variable a1) {
        int r1;
        P6any o1 = a1.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1, o1, msb_d);
        P6any n1 = GetNumber(a1, o1, out r1);

        return c.setting.MakeInt(PromoteToBigInt(r1, n1).Most_Significant_Bit_Position());
    }

    static readonly Func<Constants,Variable,Variable> bigrand_d = bigrand;
    [ImplicitConsts] public static Variable bigrand(Constants c, Variable a1) {
        // Assumes a1 is positive!
        int r1;
        P6any o1 = a1.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1, o1, bigrand_d);
        P6any n1 = GetNumber(a1, o1, out r1);

        BigInteger top = PromoteToBigInt(r1, n1);
        int bits = (int) Math.Ceiling(BigInteger.Log(top, 2));
        int bytes = bits / 8 + 1;
        byte mask = (byte) ((1 << (bits % 8)) - 1);

        byte[] b = new byte[bytes + 1];
        while (true) {
            lock (rng_lock) { rng.NextBytes(b); }
            b[bytes] = 0;
            b[bytes - 1] &= mask;
            BigInteger candidate = new BigInteger (b);
            if (candidate < top)
                return c.setting.MakeInt(candidate);
        }
    }

    [TrueGlobal] static IForeignInterpreter p5_interpreter;
    public static Variable eval_perl5(Variable v) {
        P6any o1 = v.Fetch();
        string r = o1.mo.mro_raw_Str.Get(v);

        if (p5_interpreter == null) {
            System.Reflection.Assembly a = System.Reflection.Assembly.Load("Perl5Interpreter");
            p5_interpreter = (IForeignInterpreter) a.CreateInstance("Perl5Interpreter");
        }
        return p5_interpreter.Eval(r);
    }

    // we don't need to do nominal checking stuff here because this
    // is in a method, never inlined, and as such the binder had to
    // already have been called.
    [ImplicitConsts] public static Variable complex_new(Constants c, Variable a1, Variable a2) {
        double d1 = a1.Fetch().mo.mro_raw_Numeric.Get(a1);
        double d2 = a2.Fetch().mo.mro_raw_Numeric.Get(a2);
        return c.setting.MakeComplex(d1,d2);
    }
    [ImplicitConsts] public static Variable complex_re(Constants c, Variable a1) {
        return c.setting.MakeFloat(Kernel.UnboxAny<Complex>(a1.Fetch()).re);
    }
    [ImplicitConsts] public static Variable complex_im(Constants c, Variable a1) {
        return c.setting.MakeFloat(Kernel.UnboxAny<Complex>(a1.Fetch()).im);
    }
    [ImplicitConsts] public static Variable rat_nu(Constants c, Variable a1) {
        return c.setting.MakeInt(Kernel.UnboxAny<Rat>(a1.Fetch()).num);
    }
    [ImplicitConsts] public static Variable rat_de(Constants c, Variable a1) {
        return c.setting.MakeInt(Kernel.UnboxAny<Rat>(a1.Fetch()).den);
    }
    [ImplicitConsts] public static Variable fatrat_nu(Constants c, Variable a1) {
        return c.setting.MakeInt(Kernel.UnboxAny<FatRat>(a1.Fetch()).num);
    }
    [ImplicitConsts] public static Variable fatrat_de(Constants c, Variable a1) {
        return c.setting.MakeInt(Kernel.UnboxAny<FatRat>(a1.Fetch()).den);
    }

    internal const int O_IS_GREATER = 1;
    internal const int O_IS_LESS    = 2;
    internal const int O_IS_EQUAL   = 4;
    internal const int O_IS_UNORD   = 8;
    internal const int O_COMPLEX_OK = 16;
    public static Variable numcompare(Constants c, Variable a1, Variable a2, int mask,
            Func<Constants,Variable,Variable,Variable> dl) {
        var s = c.setting;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any)) {
            Variable jr = HandleSpecial2(c, a1, a2, o1, o2, dl);
            // treat $x != $y as !($x == $y)
            if (mask == (O_IS_GREATER | O_IS_LESS | O_IS_UNORD | O_COMPLEX_OK))
                return jr.Fetch().mo.mro_raw_Bool.Get(jr) ? s.FalseV : s.TrueV;
            return jr;
        }
        int res = numcompare_core(s, a1, o1, a2, o2, (mask & O_COMPLEX_OK) != 0);

        return ((mask & res) != 0) ? s.TrueV : s.FalseV;
    }

    internal static int numcompare_core(Compartment s, Variable a1, P6any o1,
            Variable a2, P6any o2, bool complex_ok) {
        int r1, r2;
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            if (!complex_ok)
                throw new NieczaException("Complex numbers are not arithmetically ordered; use cmp if you want an arbitrary order");
            Complex v1 = PromoteToComplex(r1, n1);
            Complex v2 = PromoteToComplex(r2, n2);
            if (double.IsNaN(v1.re) || double.IsNaN(v1.im) ||
                    double.IsNaN(v2.re) || double.IsNaN(v2.im))
                return O_IS_UNORD;
            else if (v1.re != v2.re)
                return v1.re > v2.re ? O_IS_GREATER : O_IS_LESS;
            else
                return v1.im > v2.im ? O_IS_GREATER : v1.im < v2.im ? O_IS_LESS : O_IS_EQUAL;
        }
        else if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            double d1 = PromoteToFloat(r1, n1);
            double d2 = PromoteToFloat(r2, n2);
            if (double.IsNaN(d1) || double.IsNaN(d2)) return O_IS_UNORD;
            else return d1 > d2 ? O_IS_GREATER : d1 < d2 ? O_IS_LESS : O_IS_EQUAL;
        }
        else if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            r1 = BigInteger.Compare(v1.num*v2.den, v2.num*v1.den);
        }
        else if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            r1 = BigInteger.Compare(v1.num*v2.den, v2.num*v1.den);
        }
        else if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            r1 = BigInteger.Compare(PromoteToBigInt(r1, n1),
                    PromoteToBigInt(r2, n2));
        }
        else
            r1 = PromoteToFixInt(r1, n1).CompareTo(PromoteToFixInt(r2, n2));

        return (r1 > 0) ? O_IS_GREATER : (r1 < 0) ? O_IS_LESS : O_IS_EQUAL;
    }

    static readonly Func<Constants,Variable,Variable,Variable> mul_d = mul;
    [ImplicitConsts] public static Variable mul(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1,a2, o1,o2, mul_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        var s = c.setting;

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            Complex v2 = PromoteToComplex(r2, n2);
            return s.MakeComplex(v1.re*v2.re - v1.im*v2.im, v1.im*v2.re + v1.re*v2.im);
        }
        if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            return s.MakeFloat(PromoteToFloat(r1, n1) * PromoteToFloat(r2, n2));
        }
        if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            return s.MakeFatRat(v1.num*v2.num, v1.den*v2.den);
        }
        if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            return s.MakeFixRat(v1.num*v2.num, ((BigInteger)v1.den)*v2.den);
        }
        if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            return s.MakeInt(PromoteToBigInt(r1, n1) * PromoteToBigInt(r2, n2));
        }
        return s.MakeInt((long)PromoteToFixInt(r1, n1) *
                (long)PromoteToFixInt(r2, n2));
    }

    static readonly Func<Constants,Variable,Variable,Variable> divide_d = divide;
    [ImplicitConsts] public static Variable divide(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1,a2, o1,o2, divide_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        var s = c.setting;

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            Complex v2 = PromoteToComplex(r2, n2);
            double sn2 = v2.re*v2.re + v2.im*v2.im;
            return s.MakeComplex((v1.re*v2.re + v1.im*v2.im)/sn2,
                    (v2.re*v1.im - v2.im*v1.re)/sn2);
        }
        if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            return s.MakeFloat(PromoteToFloat(r1, n1) / PromoteToFloat(r2, n2));
        }
        if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            return s.MakeFatRat(v1.num*v2.den, v1.den*v2.num);
        }
        if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            return s.MakeFixRat(v1.num*v2.den, v2.num*v1.den);
        }
        if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            return s.MakeFixRat(PromoteToBigInt(r1, n1), PromoteToBigInt(r2, n2));
        }
        return s.MakeFixRat(PromoteToFixInt(r1, n1), PromoteToFixInt(r2, n2));
    }

    static readonly Func<Constants,Variable,Variable,Variable> mod_d = mod;
    [ImplicitConsts] public static Variable mod(Constants c, Variable a1, Variable a2) {
        int r1, r2;
        P6any o1 = a1.Fetch(), o2 = a2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, a1,a2, o1,o2, mod_d);
        P6any n1 = GetNumber(a1, o1, out r1);
        P6any n2 = GetNumber(a2, o2, out r2);
        var s = c.setting;

        if (r1 == NR_COMPLEX || r2 == NR_COMPLEX) {
            throw new NieczaException("Modulus operation not defined with complex arguments");
        }
        if (r1 == NR_FLOAT || r2 == NR_FLOAT) {
            double v1 = PromoteToFloat(r1, n1);
            double v2 = PromoteToFloat(r2, n2);
            return s.MakeFloat(v1 - v2 * Math.Floor(v1 / v2));
        }
        if (r1 == NR_FATRAT || r2 == NR_FATRAT) {
            FatRat v1 = PromoteToFatRat(r1, n1);
            FatRat v2 = PromoteToFatRat(r2, n2);

            BigInteger c1 = v1.num*v2.den;
            BigInteger c2 = v2.num*v1.den;
            BigInteger cd = v1.den*v2.den;

            BigInteger rem;
            BigInteger red = BigInteger.DivRem(c1, c2, out rem);
            if (c2.Sign > 0 && rem.Sign < 0) red--;
            if (c2.Sign < 0 && rem.Sign > 0) red--;

            return s.MakeFatRat(c1 - red*cd, cd);
        }
        if (r1 == NR_FIXRAT || r2 == NR_FIXRAT) {
            Rat v1 = PromoteToFixRat(r1, n1);
            Rat v2 = PromoteToFixRat(r2, n2);

            BigInteger c1 = v1.num*v2.den;
            BigInteger c2 = v2.num*v1.den;
            BigInteger cd = ((BigInteger)v1.den)*v2.den;

            BigInteger rem;
            BigInteger red = BigInteger.DivRem(c1, c2, out rem);
            if (c2.Sign > 0 && rem.Sign < 0) red--;
            if (c2.Sign < 0 && rem.Sign > 0) red--;

            return s.MakeFixRat(c1 - red*c2, cd);
        }
        if (r1 == NR_BIGINT || r2 == NR_BIGINT) {
            BigInteger v1 = PromoteToBigInt(r1, n1);
            BigInteger v2 = PromoteToBigInt(r2, n2);
            BigInteger rem;
            BigInteger red = BigInteger.DivRem(v1, v2, out rem);
            if (v2.Sign > 0 && rem.Sign < 0) red--;
            if (v2.Sign < 0 && rem.Sign > 0) red--;
            return s.MakeInt(v1 - v2*red);
        }
        {
            long v1 = PromoteToFixInt(r1, n1);
            long v2 = PromoteToFixInt(r2, n2);
            long rem;
            long red = Math.DivRem(v1, v2, out rem);
            if (v2 > 0 && rem < 0) red--;
            if (v2 < 0 && rem > 0) red--;
            return s.MakeInt(v1 - v2*red);
        }
    }

    // this is only called from .Int
    [ImplicitConsts] public static Variable coerce_to_int(Constants c, Variable a1) {
        int r1;
        P6any o1 = a1.Fetch();
        P6any n1 = GetNumber(a1, o1, out r1);

        if (r1 == NR_FLOAT) {
            double v1 = PromoteToFloat(r1, n1);
            if (Double.IsNaN(v1) || Double.IsNegativeInfinity(v1) || Double.IsPositiveInfinity(v1)) {
                return c.setting.MakeFloat(v1);
            }
        }

        int small; BigInteger big;
        return GetAsInteger(a1, out small, out big) ?
            c.setting.MakeInt(big) : c.setting.MakeInt(small);
    }

    // only called from infix for now
    // when it's not, it'll need to be split anyway, I think
    // I fumbled spec reading - only 4 and 5 are actually needed
    [ImplicitConsts] public static Variable divop(Constants c, int opc, Variable a1, Variable a2) {
        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(a1, out small1, out big1);
        bool b2 = GetAsInteger(a2, out small2, out big2);
        var s = c.setting;

        if (b1 || b2 || small1 == int.MinValue || small2 == int.MinValue) {
            if (!b1) big1 = small1;
            if (!b2) big2 = small2;
            BigInteger rem;
            BigInteger red = BigInteger.DivRem(big1, big2, out rem);
            if (opc >= 4 && big2.Sign > 0 && rem.Sign < 0) {
                red--;
                rem += big2;
            }
            if (opc >= 4 && big2.Sign < 0 && rem.Sign > 0) {
                red--;
                rem += big2;
            }
            switch (opc & 3) {
                case 0: return s.MakeInt(red);
                case 1: return s.MakeInt(rem);
                default: return s.MakeParcel(s.MakeInt(red), s.MakeInt(rem));
            }
        } else {
            int rem = small1 % small2;
            int red = small1 / small2;
            if (opc >= 4 && small2 > 0 && rem < 0) {
                red--;
                rem += small2;
            }
            if (opc >= 4 && small2 < 0 && rem > 0) {
                red--;
                rem += small2;
            }
            switch (opc & 3) {
                case 0: return s.MakeInt(red);
                case 1: return s.MakeInt(rem);
                default: return s.MakeParcel(s.MakeInt(red), s.MakeInt(rem));
            }
        }
    }

    // called from .Num
    [ImplicitConsts] public static Variable coerce_to_num(Constants c, Variable a1) {
        int r1;
        P6any n1 = GetNumber(a1, a1.Fetch(), out r1);

        if (r1 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            if (v1.im != 0)
                throw new NieczaException("Complex cannot be used here");
            return c.setting.MakeFloat(v1.re);
        } else {
            return c.setting.MakeFloat(PromoteToFloat(r1, n1));
        }
    }

    static readonly Func<Constants,Variable,Variable> sqrt_d = sqrt;
    [ImplicitConsts] public static Variable sqrt(Constants c, Variable a1) {
        P6any o1 = a1.Fetch();
        int r1;
        if (!o1.mo.is_any)
            return HandleSpecial1(c, a1,o1, sqrt_d);
        P6any n1 = GetNumber(a1, o1, out r1);

        if (r1 == NR_COMPLEX) {
            Complex v1 = PromoteToComplex(r1, n1);
            double angle = Math.Atan2(v1.im, v1.re) / 2;
            double mag = Math.Sqrt(Math.Sqrt(v1.im*v1.im + v1.re*v1.re));
            return c.setting.MakeComplex(mag * Math.Cos(angle), mag * Math.Sin(angle));
        } else {
            double val = PromoteToFloat(r1, n1);
            return c.setting.MakeFloat(Math.Sqrt(val));
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> numand_d = numand;
    [ImplicitConsts] public static Variable numand(Constants c, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, v1,v2, o1,o2, numand_d);

        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        bool b2 = GetAsInteger(v2, out small2, out big2);
        var s = c.setting;

        if (b1 || b2 || small1 == int.MinValue || small2 == int.MinValue) {
            if (!b1) big1 = small1;
            if (!b2) big2 = small2;
            return s.MakeInt(big1 & big2);
        } else {
            return s.MakeInt(small1 & small2);
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> numor_d = numor;
    [ImplicitConsts] public static Variable numor(Constants c, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, v1,v2, o1,o2, numor_d);

        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        bool b2 = GetAsInteger(v2, out small2, out big2);
        var s = c.setting;

        if (b1 || b2 || small1 == int.MinValue || small2 == int.MinValue) {
            if (!b1) big1 = small1;
            if (!b2) big2 = small2;
            return s.MakeInt(big1 | big2);
        } else {
            return s.MakeInt(small1 | small2);
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> numxor_d = numxor;
    [ImplicitConsts] public static Variable numxor(Constants c, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, v1,v2, o1,o2, numxor_d);

        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        bool b2 = GetAsInteger(v2, out small2, out big2);
        var s = c.setting;

        if (b1 || b2 || small1 == int.MinValue || small2 == int.MinValue) {
            if (!b1) big1 = small1;
            if (!b2) big2 = small2;
            return s.MakeInt(big1 ^ big2);
        } else {
            return s.MakeInt(small1 ^ small2);
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> numlshift_d = numlshift;
    [ImplicitConsts] public static Variable numlshift(Constants c, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, v1,v2, o1,o2, numlshift_d);

        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        bool b2 = GetAsInteger(v2, out small2, out big2);
        var s = c.setting;

        if (b2) {
            throw new NieczaException("Shift too many places to the right");
        }

        if (b1 || small1 == int.MinValue) {
            if (!b1) big1 = small1;
            return s.MakeInt(big1 << small2);
        } else {
            return s.MakeInt(small1 << small2);
        }
    }

    static readonly Func<Constants,Variable,Variable,Variable> numrshift_d = numrshift;
    [ImplicitConsts] public static Variable numrshift(Constants c, Variable v1, Variable v2) {
        P6any o1 = v1.Fetch(); P6any o2 = v2.Fetch();
        if (!(o1.mo.is_any && o2.mo.is_any))
            return HandleSpecial2(c, v1,v2, o1,o2, numrshift_d);

        int small1, small2; BigInteger big1, big2;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        bool b2 = GetAsInteger(v2, out small2, out big2);
        var s = c.setting;

        if (b2) {
            throw new NieczaException("Shift too many places to the left");
        }

        if (b1 || small1 == int.MinValue) {
            if (!b1) big1 = small1;
            return s.MakeInt(big1 >> small2);
        } else {
            return s.MakeInt(small1 >> small2);
        }
    }

    static readonly Func<Constants,Variable,Variable> numcompl_d = numcompl;
    [ImplicitConsts] public static Variable numcompl(Constants c, Variable v1) {
        P6any o1 = v1.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, v1,o1, numcompl_d);

        int small1; BigInteger big1;
        bool b1 = GetAsInteger(v1, out small1, out big1);
        var s = c.setting;

        if (b1 || small1 == int.MinValue) {
            if (!b1) big1 = small1;
            return s.MakeInt(~big1);
        } else {
            return s.MakeInt(~small1);
        }
    }

    // only called from .Rat
    [ImplicitConsts] public static Variable rat_approx(Constants c, Variable v1, Variable v2) {
        NominalCheck("$x", v1);
        NominalCheck("$y", v2);

        BigInteger nc, dc, ne, de, na, da;
        GetAsRational(v1, out nc, out dc);
        GetAsRational(v2, out ne, out de);

        if (ne != 0) {
            RatApproxer.Simplest(nc*de-ne*dc,dc*de,nc*de+ne*dc,dc*de,out na,out da);
        } else {
            na = nc; da = dc;
        }

        SimplifyFrac(ref na, ref da);

        // since the user controls the denominator size here, use FatRat freely
        // XXX: is it appropriate to return FatRat from a method named Rat?
        ulong sda;
        if (da.AsUInt64(out sda)) {
            return c.setting.MakeFixRat(na,da);
        } else {
            return c.setting.MakeFatRat(na,da);
        }
    }

    [ImplicitConsts] public static Variable postinc(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        v.AssignO(o1.mo.mro_succ.Get(v), false);

        if (!o1.IsDefined()) {
            if (o1.Isa(c.setting.BoolMO))
                o1 = c.setting.FalseV;
            else
                o1 = Kernel.BoxRaw<int>(0, c.setting.IntMO);
        }

        return o1;
    }

    public static Variable preinc(Variable v) {
        v.AssignO(v.Fetch().mo.mro_succ.Get(v), false);
        return v;
    }

    [ImplicitConsts] public static Variable postdec(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        v.AssignO(o1.mo.mro_pred.Get(v), false);

        if (!o1.IsDefined()) {
            if (o1.Isa(c.setting.BoolMO))
                o1 = c.setting.FalseV;
            else
                o1 = Kernel.BoxRaw<int>(0, c.setting.IntMO);
        }

        return o1;
    }

    public static Variable predec(Variable v) {
        v.AssignO(v.Fetch().mo.mro_pred.Get(v), false);
        return v;
    }

    [ImplicitConsts] public static Variable not(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        bool r = o1.mo.mro_raw_Bool.Get(v);
        return r ? c.setting.FalseV : c.setting.TrueV;
    }

    static readonly Func<Constants,Variable,Variable> chars_d = chars;
    [ImplicitConsts] public static Variable chars(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, v,o1, chars_d);

        string r = o1.mo.mro_raw_Str.Get(v);
        return c.setting.MakeInt(r.Length);
    }

    static readonly Func<Constants,Variable,Variable> codes_d = codes;
    [ImplicitConsts] public static Variable codes(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, v,o1, codes_d);

        string r = o1.mo.mro_raw_Str.Get(v);

        int i = 0;
        foreach (char ch in r)
            if (((uint)(ch - 0xDC00)) >= 0x400)
                i++;

        return c.setting.MakeInt(i);
    }

    static readonly Func<Constants,Variable,Variable> ord_d = ord;
    [ImplicitConsts] public static Variable ord(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, v,o1, ord_d);

        string r = o1.mo.mro_raw_Str.Get(v);
        // XXX Failure
        if (r.Length == 0) return c.setting.AnyP;
        else if (r.Length >= 2 &&
                r[0] >= (char)0xD800 && r[0] <= (char)0xDBFF &&
                r[1] >= (char)0xDC00 && r[1] <= (char)0xDFFF)
            return c.setting.MakeInt((0x10000 - 0xDC00) +
                    ((int)r[0] - 0xD800) * 0x400 + (int)r[1]);
        return c.setting.MakeInt((int)r[0]);
    }

    static readonly Func<Constants,Variable,Variable> chr_d = chr;
    [ImplicitConsts] public static Variable chr(Constants c, Variable v) {
        P6any o1 = v.Fetch();
        if (!o1.mo.is_any)
            return HandleSpecial1(c, v,o1, chr_d);

        int r = (int)o1.mo.mro_raw_Numeric.Get(v);
        if (r >= 0x110000)
            return c.setting.AnyP; // XXX failure
        return Kernel.BoxAnyMO(Utils.Chr(r), c.setting.StrMO);
    }

    [ImplicitConsts] public static Variable UniCat(Constants c, Variable v) {
        P6any o1 = NominalCheck("$x", v);
        char ch = (char) o1.mo.mro_raw_Numeric.Get(v);
        int ix = (int) char.GetUnicodeCategory(ch);
        return c.setting.MakeInt(ix);
    }

    public static Variable Make(Frame fr, Variable v) {
        if (fr.info.name == "CORE make")
            fr = fr.caller;
        Cursor c = (Cursor) fr.LexicalFind("$/").Fetch();
        c.Make(v);
        return v;
    }

    public static VarDeque HashIterRaw(int mode, Variable v) {
        P6any o = NominalCheck("$x", v);
        VarHash d = Kernel.UnboxAny<VarHash>(o);
        var s = o.mo.setting;

        VarDeque lv = new VarDeque();

        foreach (KeyValuePair<string,Variable> kv in d) {
            switch (mode) {
                case 0:
                    lv.Push(s.MakeStr(kv.Key));
                    break;
                case 1:
                    lv.Push(kv.Value);
                    break;
                case 2:
                    lv.Push(s.MakeStr(kv.Key));
                    lv.Push(kv.Value);
                    break;
                case 3:
                    P6opaque p = new P6opaque(s.PairMO);
                    p.slots[0] = s.MakeStr(kv.Key);
                    p.slots[1] = kv.Value;
                    lv.Push(p);
                    break;
            }
        }
        return lv;
    }
    public static Variable HashIter(int mode, Variable v) {
        VarDeque lv = HashIterRaw(mode, v);
        P6opaque l = new P6opaque(v.Fetch().mo.setting.ListMO);
        l.slots[0] = lv;
        l.slots[1] = new VarDeque();
        return Kernel.NewRWListVar(l);
    }

    [ImplicitConsts] public static Variable path_modified(Constants c, string path) {
        long t = File.GetLastWriteTimeUtc(path).Ticks;
        return c.setting.MakeFloat(((double)(t - 621355968000000000L)) / 10000000.0);
    }

    [ImplicitConsts] public static Variable now(Constants c) {
        long t = DateTime.UtcNow.Ticks;
        return c.setting.MakeFloat(((double)(t - 621355968000000000L)) / 10000000.0);
    }

    [ImplicitConsts] public static Variable times(Constants c) {
        Process p = Process.GetCurrentProcess();
        Variable[] ret = new Variable[4];
        ret[0] = ret[2] = c.setting.MakeFloat(((double)p.UserProcessorTime.Ticks) / 10000000.0);
        ret[1] = ret[3] = c.setting.MakeFloat(((double)p.PrivilegedProcessorTime.Ticks) / 10000000.0);
        return c.setting.MakeParcel(ret);
    }

    [TrueGlobal]
    private static object rng_lock = new object();
    private static Random rng = new Random();

    [ImplicitConsts] public static Variable rand(Constants c) {
        double i;
        lock (rng_lock) { i = rng.NextDouble(); }
        return c.setting.MakeFloat(i);
    }

    public static void srand(int seed) {
        lock (rng_lock) { rng = new Random(seed); }
    }

    public static void srand_time() {
        lock (rng_lock) { rng = new Random(); }
    }

    public static bool path_any_exists(string path) {
        return File.Exists(path) || Directory.Exists(path);
    }

    public static void path_unlink(string path) {
        File.Delete(path);
    }

    public static string cwd_path() {
        return Directory.GetCurrentDirectory();
    }

    public static void path_chdir(string path) {
        Directory.SetCurrentDirectory(path);
    }

    public static void path_mkdir(string path) {
        Directory.CreateDirectory(path);
    }

    public static int path_rmdir(string path) {
        return PosixWrapper.rmdir(path);
    }

    public static void path_copy(string from_path, string to_path) {
        System.IO.File.Copy(from_path, to_path, true);
    }

    public static int command_system(string command) {
        return PosixWrapper.system(command);
    }

    public static int command_run(Variable argv, Variable env) {
        Type Process = Type.GetType("GLib.Process,glib-sharp, Version=2.12.0.0, Culture=neutral, PublicKeyToken=35e10195dab3c99f");
        Type SpawnFlags = Type.GetType("GLib.SpawnFlags,glib-sharp, Version=2.12.0.0, Culture=neutral, PublicKeyToken=35e10195dab3c99f");
        MethodInfo spawn_sync = Process.GetMethod("SpawnSync");

        object[] arguments = new object[]{ null, UnboxLoS(argv), UnboxLoS(env), Enum.ToObject(SpawnFlags, 0), null,
                                           null, null, null };
        bool result = (bool) spawn_sync.Invoke(null, arguments);
/*        string stdout = (string) arguments[5];
        string stderr = (string) arguments[6];
        int exit_status = (int) arguments[7];
*/        return result ? 1 : 0;
    }

    public static string command_qx(string command_line) {
        Type Process;
        try {
            Process = Type.GetType("GLib.Process,glib-sharp, Version=2.12.0.0, Culture=neutral, PublicKeyToken=35e10195dab3c99f");
        } catch (Exception) {
            Process = null;
        }
        if (Process != null) {
            MethodInfo spawn_sync = Process.GetMethod("SpawnCommandLineSync");

            object[] arguments = new object[]{ command_line, null, null, null };
            bool result = (bool) spawn_sync.Invoke(null, arguments);
            return result ? (string) arguments[1] : "";
        } else {
            /* Next line should be more robust... */
            string [] args = command_line.Split(new Char[] {' '}, 2);
            Process process = new Process();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.FileName = args[0];
            if (args.Length > 1 && args[1] != "") {
                process.StartInfo.Arguments = args[1];
            }
            process.Start();
            /* Next two lines have to be in this order, not sure why */
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
    }

    public static int path_chmod(string path, double mode) {
        return PosixWrapper.chmod(path, (int) mode);
    }

    public static bool emulate_eaccess(string path, int mode) {
        uint ruid = PosixWrapper.getuid();
        uint rgid = PosixWrapper.getgid();
        uint euid = PosixWrapper.geteuid();
        uint egid = PosixWrapper.getegid();
        // this is not threadsafe, but it's how Perl 5 does it sometimes
        PosixWrapper.setreuid(euid, euid);
        PosixWrapper.setregid(egid, egid);
        int res = PosixWrapper.access(path, mode);
        PosixWrapper.setreuid(ruid, euid);
        PosixWrapper.setregid(rgid, egid);
        return res == 0;
    }

    public static bool path_access_readable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && PosixWrapper.access(path, PosixWrapper.R_OK) == 0;
    }

    public static bool path_access_writable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && PosixWrapper.access(path, PosixWrapper.W_OK) == 0;
    }

    public static bool path_access_executable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && PosixWrapper.access(path, PosixWrapper.X_OK) == 0;
    }
    
    public static bool path_eaccess_readable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && emulate_eaccess(path, PosixWrapper.R_OK);
    }

    public static bool path_eaccess_writable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && emulate_eaccess(path, PosixWrapper.W_OK);
    }

    public static bool path_eaccess_executable(string path) {
        return (File.Exists(path) || Directory.Exists(path))
            && emulate_eaccess(path, PosixWrapper.X_OK);
    }

    public static bool path_eaccess_owned(string path) {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        long[] stat = PosixWrapper.stat(path);
        return PosixWrapper.geteuid() == (uint)stat[5];
    }

    public static bool path_access_owned(string path) {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        long[] stat = PosixWrapper.stat(path);
        return PosixWrapper.getuid() == (uint)stat[5];
    }

    public static string[] UnboxLoS(Variable args) {
        List<string> la = new List<string>();
        VarDeque iter = start_iter(args);
        while (Kernel.IterHasFlat(iter, true)) {
            Variable v = iter.Shift();
            la.Add(v.Fetch().mo.mro_raw_Str.Get(v));
        }
        return la.ToArray();
    }

    public static Frame you_are_here(Frame th, string sname) {
        P6any to_call = Kernel.MakeSub(th.info.setting.setting_path[sname], th);
        return to_call.Invoke(th, Variable.None, null);
    }

    public static void RunSubtask(string file, string args) {
        System.Diagnostics.Process.Start(file, args).WaitForExit();
    }

    internal static object UpCall(Compartment c, params object[] args) {
        return c.upcall_receiver[args];
    }
    public static Frame simple_eval(Frame th, Variable str) {
        if (th.info.setting.upcall_receiver == null)
            return Kernel.Die(th, "Cannot eval; no compiler available");
        SubInfo outer = th.caller.info;
        object r = UpCall(th.info.setting, "EVAL",
                str.Fetch().mo.mro_raw_Str.Get(str),
                new Niecza.CLRBackend.Handle(outer),
                new Niecza.CLRBackend.Handle(th.caller)
                );
        if (r is Exception)
            return Kernel.Die(th, ((Exception)r).Message);
        P6any sub = Kernel.MakeSub(((RuntimeUnit)Niecza.CLRBackend.Handle.Unbox(r)).mainline, th.caller);
        return sub.Invoke(th, Variable.None, null);
    }

    internal static P6any compile_bind_regex(Frame th, string code) {
        if (th.info.rx_compile_cache == null)
            th.info.rx_compile_cache = new Dictionary<string, SubInfo>();
        SubInfo main;
        // TODO: it would be better if the compiler could be modified to
        // compile a regex directly as the mainline
        if (!th.info.rx_compile_cache.TryGetValue(code, out main)) {
            if (th.info.setting.upcall_receiver == null)
                throw new NieczaException("Cannot eval; no compiler available");
            object r = UpCall(th.info.setting, "EVAL",
                    "regex {" + code + "}",
                    new Niecza.CLRBackend.Handle(th.info));
            if (r is Exception)
                throw new NieczaException(((Exception)r).Message);
            main = ((RuntimeUnit)Niecza.CLRBackend.Handle.Unbox(r)).mainline;
            th.info.rx_compile_cache[code] = main;
        }
        P6any sub = Kernel.MakeSub(main, th);
        return InvokeSub(sub).Fetch();
    }

    [ImplicitConsts] public static Variable pair(Constants c, Variable key, Variable value) {
        return c.setting.MakePair(key, value);
    }

    public static VarDeque start_iter(Variable thing) {
        if (thing.List)
            return thing.Fetch().mo.mro_raw_iterator.Get(thing);
        else
            return new VarDeque(thing);
    }

    [ImplicitConsts] public static Variable array_constructor(Constants c, Variable bits) {
        VarDeque rest  = start_iter(bits);
        VarDeque items = new VarDeque();
        while (Kernel.IterHasFlat(rest, true))
            items.Push(Kernel.NewMuScalar(rest.Shift().Fetch()));
        return c.setting.MakeArray(items, rest);
    }

    public static string frame_subname(Frame fr) {
        return fr.info.name.Substring(fr.info.name.IndexOf(" ")+1);
    }

    [ImplicitConsts] public static Variable count(Constants c, P6any fcni) {
        int i = get_count(fcni);
        return (i == int.MaxValue) ? c.setting.MakeFloat(double.PositiveInfinity) :
            c.setting.MakeInt(i);
    }
    public static int get_count(P6any fcni) {
        if (!fcni.Isa(fcni.mo.setting.CodeMO))
            return 1; // can't introspect fake subs (?)
        return sig_count(Kernel.GetInfo(fcni).sig);
    }
    public static int sig_count(P6any si) {
        if (si == null)
            return 1;
        int arity = 0;
        foreach (Parameter p in ((Signature)si).parms) {
            int fl = p.flags;
            if ((fl & (Parameter.SLURPY_CAP | Parameter.SLURPY_POS |
                    Parameter.SLURPY_PCL)) != 0)
                return int.MaxValue;
            if ((fl & Parameter.SLURPY_NAM) != 0) continue;
            if ((fl & Parameter.POSITIONAL) == 0) continue;
            arity++;
        }
        return arity;
    }

    public static int sig_arity(P6any obj) {
        int arity = 0;
        foreach (Parameter p in ((Signature)obj).parms) {
            int fl = p.flags;
            if ((fl & (Parameter.SLURPY_CAP | Parameter.SLURPY_POS |
                    Parameter.SLURPY_PCL | Parameter.SLURPY_NAM |
                    Parameter.OPTIONAL | Parameter.DEFOUTER |
                    Parameter.HASDEFAULT)) != 0)
                continue;
            if ((fl & Parameter.POSITIONAL) == 0) continue;
            arity++;
        }
        return arity;
    }
    [ImplicitConsts] public static Variable arity(Constants c, P6any fcni) {
        if (!fcni.Isa(c.setting.CodeMO))
            return c.setting.MakeInt(1); // can't introspect fake subs (?)
        SubInfo si = (SubInfo) Kernel.GetInfo(fcni);
        if (si.sig == null)
            return c.setting.MakeInt(1);
        return c.setting.MakeInt(sig_arity(si.sig));
    }

    class ItemSource {
        protected ItemSource() {}
        [Immutable]
        public static ItemSource Empty = new ItemSource();
        // main access point - return true to block (only if block is true)
        public virtual bool TryGet(out Variable[] r, bool block) {
            r = null;
            return true;
        }
        // utility to get data from a list
        protected static int TryOne(VarDeque items, bool block) {
            if (block) {
                return Kernel.IterHasFlat(items, true) ? +1 : -1;
            } else {
again:
                if (items.Count() == 0) return -1;
                Variable v = items[0];
                P6any i = v.Fetch();
                if (i.mo.HasType(i.mo.setting.IterCursorMO))
                    return 0;
                if (v.List) {
                    items.Shift();
                    items.UnshiftD(i.mo.mro_raw_iterator.Get(v));
                    goto again;
                }
                return +1;
            }
        }
    }

    class BatchSource: ItemSource {
        int arity;
        VarDeque items;

        public BatchSource(int count, VarDeque items) {
            this.arity = count;
            this.items = items;
        }

        public override bool TryGet(out Variable[] r, bool block) {
            r = null;
            List<Variable> pen = new List<Variable>();
            while (pen.Count < arity) {
                switch (TryOne(items, block)) {
                    case -1: goto nomore;
                    case 0:
                        for (int i = pen.Count - 1; i >= 0; i--)
                            items.Unshift(pen[i]);
                        return false;
                    case +1: pen.Add(items.Shift()); break;
                }
            }
nomore:
            if (pen.Count != 0)
                r = pen.ToArray();
            return true;
        }
    }

    class ZipSource : ItemSource {
        VarDeque[] sources;
        public ZipSource(Variable[] pcl) {
            sources = new VarDeque[pcl.Length];
            for (int i = 0; i < pcl.Length; i++)
                sources[i] = start_iter(pcl[i]);
        }

        public override bool TryGet(out Variable[] r, bool block) {
            r = null;
            for (int i = 0; i < sources.Length; i++)
                switch (TryOne(sources[i], block)) {
                    case -1: return true;
                    case  0: return false;
                }
            if (sources.Length == 0)
                return true; // empty zip should return immediately
            r = new Variable[sources.Length];
            for (int i = 0; i < sources.Length; i++)
                r[i] = sources[i].Shift();
            return true;
        }
    }

    class CrossSource: ItemSource {
        VarDeque[] basic;
        VarDeque[] iter;
        Variable[] basic_top;
        Variable[] iter_top;
        // 0=init 1=end i=advance wheel i-2
        int state;

        public CrossSource(Variable[] pcl) {
            basic = new VarDeque[pcl.Length];
            iter  = new VarDeque[pcl.Length];
            basic_top = new Variable[pcl.Length];
            iter_top  = new Variable[pcl.Length];
            for (int i = 0; i < pcl.Length; i++) {
                iter[i] = start_iter(pcl[i]);
            }
        }

        public override bool TryGet(out Variable[] r, bool block) {
            r = null;
            if (state == 0) {
                // Make sure all the lists are non-empty.
                for (int i = 0; i < iter.Length; i++) {
                    switch (TryOne(iter[i], block)) {
                        case -1: return true;
                        case 0:  return false;
                        case 1:  break;
                    }
                }
                for (int i = 0; i < iter.Length; i++) {
                    iter_top[i] = iter[i].Shift();
                    if (i != 0) {
                        basic[i] = new VarDeque(iter[i]);
                        basic_top[i] = iter_top[i];
                    }
                }
            }
            else if (state == 1) {
                return true;
            }
            else {
again:
                int wheel = state - 2;
                switch (TryOne(iter[wheel], block)) {
                    case 0:  return false;
                    case +1:
                        iter_top[wheel] = iter[wheel].Shift();
                        break;
                    case -1:
                        if (wheel == 0) return true;
                        iter[wheel] = new VarDeque(basic[wheel]);
                        iter_top[wheel] = basic_top[wheel];
                        state--;
                        goto again;
                }
            }
            r = new Variable[iter_top.Length];
            for (int i = 0; i < iter_top.Length; i++)
                r[i] = iter_top[i];
            state = iter_top.Length + 1;
            return true;
        }
    }

    internal static Frame CommonMEMap_C(Frame th) {
        ItemSource src = (ItemSource) th.lex0;
        VarDeque outq = (VarDeque) th.lex1;
        object fnc = th.lex2;
        int tailmode = th.lexi0;
        var setting = th.info.setting;

        switch (th.ip) {
            case 0:
                Variable[] pen;
                if (!src.TryGet(out pen, tailmode != 0)) {
                    P6opaque thunk = new P6opaque(setting.GatherIteratorMO);
                    th.coro_return = th;
                    th.MarkSharedChain();
                    thunk.slots[0] = Kernel.NewMuScalar(th);
                    thunk.slots[1] = Kernel.NewMuScalar(setting.AnyP);
                    P6opaque lst = new P6opaque(setting.ListMO);
                    lst.slots[0] = outq;
                    lst.slots[1] = new VarDeque(thunk);
                    th.caller.resultSlot = Kernel.NewRWListVar(lst);
                    th.lexi0 = 1;
                    return th.Return();
                }
                if (pen == null) {
                    if (tailmode != 0)
                        return Kernel.Take(th, setting.EMPTYP);
                    P6opaque lst = new P6opaque(setting.ListMO);
                    lst.slots[0] = outq;
                    lst.slots[1] = new VarDeque();
                    th.caller.resultSlot = Kernel.NewRWListVar(lst);
                    return th.Return();
                }
                th.lex3 = pen;
                th.ip = 1;
                goto case 1;
            case 1:
                th.ip = 2;
                if (fnc is P6any) {
                    return ((P6any)fnc).Invoke(th, (Variable[])th.lex3, null);
                } else if (fnc == null) {
                    th.resultSlot = setting.MakeParcel((Variable[]) th.lex3);
                    goto case 2;
                } else {
                    th.resultSlot = ((Func<Variable,Variable>)fnc).Invoke(
                        ((Variable[])th.lex3)[0]);
                    goto case 2;
                }
            case 2:
                if (tailmode != 0) {
                    th.ip = 0;
                    return Kernel.Take(th, (Variable)th.resultSlot);
                } else {
                    outq.Push((Variable) th.resultSlot);
                    th.ip = 0;
                    goto case 0;
                }
            case 3:
                th.lex0 = src = ItemSource.Empty;
                th.ip = 0;
                goto case 0;
            default:
                return Kernel.Die(th, "Invalid IP");
        }
    }

    public static Frame MEMap(Frame th, Variable[] lst) {
        VarDeque iter = new VarDeque(lst);
        Variable fcn = iter.Shift();
        P6any fcni = fcn.Fetch();
        int arity = get_count(fcni);
        var setting = th.info.setting;

        Frame fr = th.MakeChild(null, setting.CommonMEMap_I, setting.AnyP);
        fr.lexi0 = 0;
        fr.lex0 = new BatchSource(arity, iter);
        fr.lex1 = new VarDeque();
        fr.lex2 = fcni;
        return fr;
    }

    public static Frame MEMap_for_each(Frame th, P6any lst,
            Func<Variable,Variable> fcn) {
        VarDeque iter = new VarDeque(Kernel.NewRWListVar(lst));
        var setting = th.info.setting;

        Frame fr = th.MakeChild(null, setting.CommonMEMap_I, setting.AnyP);
        fr.lexi0 = 0;
        fr.lex0 = new BatchSource(1, iter);
        fr.lex1 = new VarDeque();
        fr.lex2 = fcn;
        return fr;
    }

    static P6any ExtractWith(bool with, ref Variable[] pcl) {
        if (!with) return null;
        Variable[] opcl = pcl;
        pcl = new Variable[pcl.Length - 1];
        for (int j = 0; j < pcl.Length; j++)
            pcl[j] = opcl[j+1];
        return opcl[0].Fetch();
    }

    public static Frame MEZip(Frame th, bool with, Variable[] pcl) {
        var setting = th.info.setting;
        Frame fr = th.MakeChild(null, setting.CommonMEMap_I, setting.AnyP);
        Kernel.SetTopFrame(fr);
        fr.lexi0 = 0;
        fr.lex2 = ExtractWith(with, ref pcl);
        fr.lex0 = new ZipSource(pcl);
        fr.lex1 = new VarDeque();
        return fr;
    }

    public static Frame MECross(Frame th, bool with, Variable[] pcl) {
        var setting = th.info.setting;
        Frame fr = th.MakeChild(null, setting.CommonMEMap_I, setting.AnyP);
        Kernel.SetTopFrame(fr);
        fr.lexi0 = 0;
        fr.lex2 = ExtractWith(with, ref pcl);
        fr.lex0 = new CrossSource(pcl);
        fr.lex1 = new VarDeque();
        return fr;
    }

    internal static Frame CommonGrep_C(Frame th) {
        VarDeque src = (VarDeque) th.lex0;
        VarDeque outq = (VarDeque) th.lex1;
        Variable flt = (Variable) th.lex2;
        int tailmode = th.lexi0;
        var setting = th.info.setting;

        switch (th.ip) {
            case 0:
                Variable pen = null;
                while (pen == null) {
                    if (tailmode != 0) {
                        if (!Kernel.IterHasFlat(src, false)) break;
                    } else {
                        if (src.Count() == 0) break;
                        if (src[0].Fetch().mo.HasType(setting.IterCursorMO)) {
                            P6opaque thunk = new P6opaque(setting.GatherIteratorMO);
                            th.coro_return = th;
                            th.MarkSharedChain();
                            thunk.slots[0] = Kernel.NewMuScalar(th);
                            thunk.slots[1] = Kernel.NewMuScalar(setting.AnyP);
                            P6opaque lst = new P6opaque(setting.ListMO);
                            lst.slots[0] = outq;
                            lst.slots[1] = new VarDeque(thunk);
                            th.caller.resultSlot = Kernel.NewRWListVar(lst);
                            th.lexi0 = 1;
                            return th.Return();
                        }
                    }
                    pen = src.Shift();
                }
                if (pen == null) {
                    if (tailmode != 0)
                        return Kernel.Take(th, setting.EMPTYP);
                    P6opaque lst = new P6opaque(setting.ListMO);
                    lst.slots[0] = outq;
                    lst.slots[1] = new VarDeque();
                    th.caller.resultSlot = Kernel.NewRWListVar(lst);
                    return th.Return();
                }
                th.lex3 = pen;
                th.ip = 1;
                goto case 1;
            case 1:
                th.ip = 2;
                return flt.Fetch().InvokeMethod(th, "ACCEPTS",
                        new Variable[] { flt, (Variable)th.lex3 }, null);
            case 2:
                Variable r = (Variable) th.resultSlot;
                if (!r.Fetch().mo.mro_raw_Bool.Get(r)) {
                    th.ip = 0;
                    goto case 0;
                }
                if (tailmode != 0) {
                    th.ip = 0;
                    return Kernel.Take(th, (Variable)th.lex3);
                } else {
                    outq.Push((Variable) th.lex3);
                    th.ip = 0;
                    goto case 0;
                }
            case 3:
                th.lex0 = src = new VarDeque();
                th.ip = 0;
                goto case 0;
            default:
                return Kernel.Die(th, "Invalid IP");
        }
    }
    public static Frame MEGrep(Frame th, Variable[] lst) {
        VarDeque iter = new VarDeque(lst);
        Variable fcn = iter.Shift();
        iter = Kernel.IterFlatten(iter);
        var setting = th.info.setting;

        Frame fr = th.MakeChild(null, setting.CommonGrep_I, setting.AnyP);
        fr.lexi0 = 0;
        fr.lex0 = iter;
        fr.lex1 = new VarDeque();
        fr.lex2 = fcn;
        return fr;
    }

    public static Frame CallNext(Frame th, P6any cap) {
        Frame to = th;
        while (to != null && to.curDisp == null)
            to = to.caller;
        if (to == null || to.curDisp.next == null)
            return Kernel.Die(th, "No next function to call!");

        DispatchEnt de = to.curDisp.next;
        P6opaque o = cap as P6opaque;

        Variable[] p = to.pos;
        VarHash n    = to.named;
        if (o != null) {
            p = (Variable[]) o.slots[0];
            n = o.slots[1] as VarHash;
        }

        return de.info.SetupCall(th, de.outer, de.ip6, p, n, false, de);
    }

    public static bool obj_can(P6any obj, string mname) {
        return obj.mo.mro_methods.ContainsKey(mname);
    }

    // Used mostly to initialize $*PID et al
    [TrueGlobal] public static string programName;
    [TrueGlobal] public static string execName;

    [ImplicitConsts] public static Variable getenv(Constants c, string str) {
        return Kernel.BoxAnyMO(Environment.GetEnvironmentVariable(str), c.setting.StrMO);
    }

    public static void setenv(string key, string val) {
        Environment.SetEnvironmentVariable(key, val);
    }

    [ImplicitConsts] public static Variable sysquery(Constants c, int ix) {
        var s = c.setting;
        switch (ix) {
            case 0: return s.BoxLoS(Kernel.commandArgs);
            case 1: return s.MakeStr(programName ?? AppDomain.CurrentDomain.FriendlyName);
            case 2: return s.MakeStr(execName);
            case 3: return s.MakeStr(AppDomain.CurrentDomain.BaseDirectory);
            case 4: {
                VarHash ret = new VarHash();
                foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables()) {
                    ret[(string) de.Key] = Kernel.BoxAnyMO((string)de.Value, s.StrMO);
                }
                return Kernel.BoxAnyMO(ret, s.HashMO);
            }
            case 5: return s.MakeStr(Environment.OSVersion.Platform.ToString());
            case 6: return s.MakeStr(Environment.OSVersion.Version.ToString());
            case 7: return s.MakeStr(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToString());
            default: return null;
        }
    }

    public static P6any who(P6any obj) { return obj.mo.who; }

    [ImplicitConsts] public static Variable stash_exists_key(Constants c, P6any st, string key) {
        string lkey = Kernel.UnboxAny<string>(st);
        lkey = (char)lkey.Length + lkey + key;
        return c.setting.currentGlobals.ContainsKey(lkey) ? c.setting.TrueV : c.setting.FalseV;
    }

    [ImplicitConsts] public static Variable stash_at_key(Constants c, P6any st, string key) {
        return c.setting.GetVar(Kernel.UnboxAny<string>(st), key).v;
    }

    [ImplicitConsts] public static Variable stash_bind_key(Constants c, P6any st, string key, Variable to) {
        c.setting.GetVar(Kernel.UnboxAny<string>(st), key).Bind(to);
        return to;
    }

    [ImplicitConsts] public static Variable stash_delete_key(Constants c, P6any st, string key) {
        string lkey = Kernel.UnboxAny<string>(st);
        lkey = (char)lkey.Length + lkey + key;
        StashEnt r;
        if (!c.setting.currentGlobals.TryGetValue(lkey, out r))
            return c.setting.AnyP;
        c.setting.currentGlobals.Remove(key);
        return r.v;
    }

    public static Variable pstash_at_key(P6any st, string key) {
        return Kernel.UnboxAny<StashCursor>(st).Raw(key, null);
    }

    public static Variable pstash_bind_key(P6any st, string key, Variable to) {
        return Kernel.UnboxAny<StashCursor>(st).Raw(key, to);
    }

    internal static Frame TEMP_C(Frame th) {
        ((Variable)th.outer.lex0).Store((P6any)th.outer.lex1);
        return th.Return();
    }

    [ImplicitConsts] public static Variable temporize(Constants c, Variable v, Frame fr, int mode) {
        int type = (mode & 1) != 0 ? LeaveHook.UNDO : LeaveHook.UNDO + LeaveHook.KEEP;
        if ((mode & 2) != 0) {
            fr.PushLeave(type, v.Fetch());
        }
        else if (v.List) {
            fr.PushLeave(type, InvokeMethod("TEMP", v).Fetch());
        }
        else {
            // this is quite dubious really, Frame and LexPad should be separated
            Frame o = new Frame(null, null, c.setting.TEMP_SI, null);
            o.lex0 = v;
            o.lex1 = v.Fetch();
            fr.PushLeave(type, Kernel.MakeSub(c.setting.TEMP_SI, o));
        }

        return v;
    }

    internal static Frame RunCATCH_C(Frame th) {
        // ENTRY  lex0 : CATCH lambda (decontainerized)
        //        lex1 : exception payload (@! Array)
        // EXIT   ret  : new @!; will catch if false
        // LEX    lex2 : @*unhandled
        //        lex3 : $current
        //        lex4 : @! iterator
        // note, compiler munges catch/control lambdas to return True
        //    if exitted via succeed
        // note2, any exception thrown from under RunCATCH_I will be caught
        //    and pushed onto @*unhandled along with $current, and next;

        // -> $handler, @! { #0
        //    my @*unhandled;
        //    for @! -> $current { #N=1 R=2
        //        $handler.($current) || push @*unhandled, $current
        //    } #L=4
        //    @*unhandled;
        // }

        // $! will be set to munged @! if we return nothing

        Variable t1, t2;
        VarDeque u1;
        P6any v1;
        switch (th.ip) {
            case 0:
                th.lex2 = th.info.setting.CreateArray();
                t1 = (Variable)th.lex1;
                th.lex4 = t1.Fetch().mo.mro_raw_iterator.Get(t1);
                goto case 1;

            case 1:
                u1 = (VarDeque)th.lex4;
                if (!Kernel.IterHasFlat(u1, true)) goto case 4;
                th.lex3 = u1.Shift();
                goto case 2;

            case 2:
                t1 = (Variable)th.lex3;
                v1 = (P6any)th.lex0;
                th.ip = 3;
                return v1.Invoke(th, new Variable[] { t1 }, null);

            case 3:
                t1 = (Variable)th.resultSlot;
                if (t1.Fetch().mo.mro_raw_Bool.Get(t1))
                    goto case 1; // yay handled
                t1 = (Variable)th.lex3;
                t2 = (Variable)th.lex2;
                t2.Fetch().mo.mro_push.Invoke(t2, new Variable[] { t1 });
                goto case 1;

            case 4:
                th.caller.resultSlot = th.lex2;
                return th.Return();

            default:
                return Kernel.Die(th, "Invalid IP");
        }
    }

    internal static string DumpVar(Variable v) {
        string ret;
        try {
            Variable p = InvokeMethod("perl", v);
            ret = p.Fetch().mo.mro_raw_Str.Get(p);
        } catch (Exception ex) {
            ret = "(stringification failed: " + ex.ToString() +  ")";
        }
        return ret;
    }

    [ImplicitConsts] public static Variable dir(Constants c, string s) {
        string[] raw = Directory.GetFileSystemEntries(s);
        string[] forperl = new string[raw.Length + 2];
        forperl[0] = "."; forperl[1] = "..";
        for (int i = 0; i < raw.Length; i++) {
            int ix = raw[i].LastIndexOf(Path.DirectorySeparatorChar);
            forperl[i+2] = (ix >= 0) ? raw[i].Substring(ix+1) : raw[i];
        }
        return c.setting.BoxLoS(forperl);
    }

    public static Thread start_p6_thread(P6any sub) {
        Frame th = Kernel.GetTopFrame();
        th.MarkSharedChain();
        Thread thr = new Thread(delegate () {
                Kernel.SetupThreadParent(th);
                InvokeSub(sub);
            });
        thr.Start();
        return thr;
    }

    [ImplicitConsts] public static Variable sleep(Constants c, double secs) {
        Stopwatch stopwatch = new Stopwatch();

        stopwatch.Start();
        Thread.Sleep((int)(secs * 1000.0));
        stopwatch.Stop();

        return c.setting.MakeFloat(stopwatch.Elapsed.TotalSeconds);
    }

    public static void pun_helper(Variable pun, Variable vname, Frame fr) {
        string name = vname.Fetch().mo.mro_raw_Str.Get(vname);
        fr.pos[0] = pun;
        fr.curDisp = new DispatchEnt(pun.Fetch().mo.FindMethod(name));
    }

    public static Frame dispatch_fromtype(Frame th) {
        Variable[] npos = new Variable[th.pos.Length - 2];
        STable from = th.pos[1].Fetch().mo;
        string name = th.pos[2].Fetch().mo.mro_raw_Str.Get(th.pos[2]);
        npos[0] = th.pos[0];
        Array.Copy(th.pos, 3, npos, 1, npos.Length - 1);

        if (!npos[0].Fetch().Does(from)) {
            return Kernel.Die(th, "Cannot dispatch to a method on " +
                from.name + " because it is not inherited or done by " +
                npos[0].Fetch().mo.name);
        }

        var de = from.FindMethod(name);
        if (de != null) {
            return de.info.SetupCall(th.Return(), de.outer, de.ip6,
                        npos, th.named, false, de);
        } else {
            return Kernel.Die(th, "Unable to resolve method " + name + " via " +
                    from.name);
        }
    }

    public static void exit(int code) {
        // Hack - at least some versions of Mono don't fire ProcessExit
        // on an explicit Exit() call
        Compartment.BeforeExit(null, null);
        Environment.Exit(code);
    }

    public static P6any repr_clone(P6any obj, VarHash mods) {
        obj = obj.ReprClone();
        if (!mods.IsNonEmpty) return obj;

        Variable arg;
        foreach (STable m in obj.mo.mo.mro) {
            foreach (P6how.AttrInfo ai in m.mo.local_attr) {
                if ((ai.flags & P6how.A_PUBLIC) == 0) continue;
                if (!mods.TryGetValue(ai.name.Substring(2), out arg)) continue;

                EstablishSlot(obj, ai, arg);
            }
        }

        return obj;
    }

    [ImplicitConsts] public static Variable mixin(Constants c, P6any obj, Variable role_list, Variable init,
            Variable newtype) {
        VarDeque iter = start_iter(role_list);
        List<STable> roles = new List<STable>();
        while (Kernel.IterHasFlat(iter, true))
            roles.Add(iter.Shift().Fetch().mo);

        STable n = new STable(c.setting, obj.mo.name + "+" + Kernel.JoinS(",", roles));

        n.how = Kernel.BoxAny<STable>(n, obj.mo.how).Fetch();
        n.typeObj = n.initObj = new P6opaque(n);
        ((P6opaque)n.typeObj).slots = null;

        n.mo.superclasses.Add(obj.mo);
        n.mo.local_roles = roles;
        n.mo.Compose();
        newtype.Store(n.typeObj);

        string aname = null;
        if (init != c.setting.AnyP) {
            if (!obj.IsDefined())
                throw new NieczaException("Cannot initialize a slot when mixing into a type object");
            if (n.mo.local_attr.Count != 1 || (n.mo.local_attr[0].flags & P6how.A_PUBLIC) == 0)
                throw new NieczaException("Role(s) being mixed in do not define precisely one, public attribute");
            aname = n.mo.local_attr[0].name;
        }

        if (obj.IsDefined()) {
            obj.ChangeType(n);

            BuildMostDerived(obj);
            if (aname != null)
                Kernel.Assign((Variable)obj.GetSlot(n, aname), init);
            return obj;
        } else {
            return n.typeObj;
        }
    }

    [ImplicitConsts] public static Variable dualvar(Constants c, Variable obj, Variable type, Variable str) {
        P6any nobj = obj.Fetch().ReprClone();
        nobj.ChangeType(type.Fetch().mo);
        nobj.SetSlot(c.setting.PseudoStrMO, "$!value",
                Kernel.UnboxAny<string>(str.Fetch()));
        return nobj;
    }

    public static void EstablishSlot(P6any n, P6how.AttrInfo ai,
            Variable vx) {
        Variable obj;
        if ((ai.flags & P6how.A_TYPE) == P6how.A_SCALAR) {
            if (ai.type == null)
                obj = Kernel.NewMuScalar(
                    vx != null ? vx.Fetch() : n.mo.setting.AnyMO.typeObj);
            else
                obj = Kernel.NewRWScalar(ai.type,
                    vx != null ? vx.Fetch() : ai.type.initObj);
        } else {
            obj = (ai.flags & P6how.A_HASH) != 0 ?
                n.mo.setting.CreateHash() : n.mo.setting.CreateArray();
            if (vx != null) {
                // https://github.com/sorear/niecza/issues/104
                if (!vx.List)
                    vx = Kernel.NewRWListVar(vx.Fetch());
                Kernel.Assign(obj, vx);
            }
        }
        n.SetSlot(ai.owner, ai.name, obj);
    }


    static void BuildMostDerived(P6any obj) {
        P6any build = null;
        foreach (P6how.MethodInfo m in obj.mo.mo.lmethods) {
            if (m.Name() == "BUILD" && m.flags == P6how.V_SUBMETHOD)
                build = m.impl;
        }

        foreach (P6how.AttrInfo ai in obj.mo.mo.local_attr) {
            Variable vx = null;
            if (ai.init != null)
                vx = InvokeSub(ai.init, obj);
            EstablishSlot(obj, ai, vx);
        }

        if (build != null)
            InvokeSub(build, obj);
    }

    public static Variable enum_mixin_role(string name, P6any meth) {
        STable r = new STable(meth.mo.setting, '{' + name + '}');
        r.mo.FillRole(new STable[0], null);
        r.typeObj = r.initObj = new P6opaque(r);
        r.mo.AddMethod(0, name, meth);
        r.mo.Revalidate();
        r.SetupVTables();
        return r.typeObj;
    }

    // TODO: merge
    public static Variable cat_mixin_role(string name, P6any meth) {
        STable r = new STable(meth.mo.setting, '{' + name + '}');
        r.mo.FillRole(new STable[0], null);
        r.typeObj = r.initObj = new P6opaque(r);
        r.mo.AddMethod(P6how.M_MULTI, name, meth);
        r.mo.Revalidate();
        r.SetupVTables();
        return r.typeObj;
    }

    public static Variable type_mixin_role(Variable type, Variable meth) {
        STable stype = type.Fetch().mo;
        string name = stype.name;
        STable r = new STable(stype.setting, "ANON");

        r.mo.FillRole(new STable[0], null);
        r.typeObj = r.initObj = new P6opaque(r);
        r.mo.AddMethod(0, name, meth.Fetch());
        r.mo.AddMethod(P6how.V_PRIVATE, name, meth.Fetch());
        r.mo.AddAttribute(name, P6how.A_PUBLIC, null, stype);
        r.mo.Revalidate();
        return r.typeObj;
    }

    public static void raise(string sig) { PosixWrapper.raise(sig); }

    public static Variable dyngetattr(Variable obj, Variable ty, Variable name) {
        string sname = Kernel.UnboxAny<string>(name.Fetch());
        return (Variable)obj.Fetch().GetSlot(ty.Fetch().mo, sname);
    }

    [ImplicitConsts] public static Variable is_role(Constants c, Variable o) {
        int rty = o.Fetch().mo.mo.type;
        return (rty == P6how.ROLE || rty == P6how.CURRIED_ROLE || rty == P6how.PARAMETRIZED_ROLE) ? c.setting.TrueV : c.setting.FalseV;
    }

    [ImplicitConsts] public static Variable var_get_var(Constants c, Variable o) {
        return Kernel.BoxAnyMO<Variable>(o, c.setting.ScalarMO);
    }

    public class Blackhole : Variable {
        P6any value;

        private Blackhole() {}
        public Blackhole(P6any value) { this.value = value; }

        public override P6any Fetch() { return value; }
        public override void Store(P6any v) { }

        public override void Freeze(Niecza.Serialization.FreezeBuffer fb) {
            fb.Byte((byte)Niecza.Serialization.SerializationCode.Blackhole);
            fb.ObjRef(value);
        }
        internal static object Thaw(Niecza.Serialization.ThawBuffer tb) {
            var n = new Blackhole();
            tb.Register(n);
            n.value = (P6any) tb.ObjRef();
            return n;
        }
    }

    public static Variable blackhole(Variable o) {
        return new Blackhole(o.Fetch());
    }

    [ImplicitConsts] public static Variable sig_params(Constants c, P6any sig) {
        VarDeque items = new VarDeque();
        items.PushN(((Signature)sig).parms);
        return Kernel.NewRWListVar(c.setting.MakeList(items, new VarDeque()));
    }

    public static string code_name(P6any obj) {
        return Kernel.GetInfo(obj).name;
    }

    [ImplicitConsts] public static P6any code_signature(Constants c, P6any obj) {
        return Kernel.GetInfo(obj).sig ?? c.setting.AnyMO.typeObj;
    }

    public static Variable code_candidates(P6any sub) {
        // Not foolproof
        SubInfo si = Kernel.GetInfo(sub);
        VarDeque items = new VarDeque();
        if (si.param != null && si.param[0] is P6any[]) {
            foreach (P6any cand in (P6any[])si.param[0])
                if (cand != null)
                    items.Push(cand);
        } else {
            items.Push(sub);
        }
        return Kernel.NewRWListVar(si.setting.MakeList(items, new VarDeque()));
    }

    public static int param_flags(P6any param) {
        return ((Parameter)param).flags;
    }

    [ImplicitConsts] public static Variable param_names(Constants c, P6any param) {
        return c.setting.BoxLoS(((Parameter)param).names ?? new string[0]);
    }

    [ImplicitConsts] public static Variable param_type(Constants c, P6any param) {
        return (((Parameter)param).type ?? c.setting.AnyMO).typeObj;
    }

    [ImplicitConsts] public static P6any param_subsig(Constants c, P6any param) {
        var p = param as Parameter;
        if (p.post_constraints != null) {
            foreach (object o in p.post_constraints) {
                if (o is Signature)
                    return (P6any)o;
            }
        }
        return c.setting.AnyMO.typeObj;
    }

    public static Variable param_value_constraints(P6any param) {
        var p = param as Parameter;
        VarDeque items = new VarDeque();
        if (p.post_constraints != null) {
            foreach (object o in p.post_constraints) {
                if (o is Variable)
                    items.Push((Variable)o);
            }
        }
        return Kernel.NewRWListVar(param.mo.setting.MakeList(items, new VarDeque()));
    }

    public static string param_name(P6any param) {
        return ((Parameter)param).name;
    }

    public static Frame code_accepts_capture(Frame th, P6any code, P6any cap) {
        var setting = th.info.setting;
        return Kernel.GetInfo(code).SetupCall(th, Kernel.GetOuter(code), code,
            (Variable[])cap.GetSlot(setting.CaptureMO, "$!positionals"),
            (VarHash)cap.GetSlot(setting.CaptureMO, "$!named"),
            true, null);
    }

    public static System.IO.TextReader treader_stdin() {
        return new System.IO.StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
    }

    [TrueGlobal] internal static StreamWriter stdout =
        new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding);
    [TrueGlobal] internal static StreamWriter stderr =
        new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding);
    public static System.IO.TextWriter twriter_stdout() {
        stdout.AutoFlush = true; return stdout;
    }

    public static System.IO.TextWriter twriter_stderr() {
        stderr.AutoFlush = true; return stderr;
    }

    public static int ref_hash(P6any o) { return RuntimeHelpers.GetHashCode(o); }

    public static Frame ind_method_call(Frame th, StashCursor root,
            string nm, P6any cap) {
        int cut = nm.LastIndexOf("::");
        var setting = th.info.setting;
        var pos = (Variable[]) cap.GetSlot(setting.CaptureMO, "$!positionals");
        var nam = (VarHash)    cap.GetSlot(setting.CaptureMO, "$!named");

        if (cut < 0) {
            return pos[0].Fetch().InvokeMethod(th, nm, pos, nam);
        } else {
            var from = root.Indirect(nm.Substring(0, cut), false, null)
                .Fetch().mo;
            var name = nm.Substring(cut+2);

            // some code copied from dispatch_fromtype
            if (!pos[0].Fetch().Does(from)) {
                return Kernel.Die(th, "Cannot dispatch to a method on " +
                    from.name + " because it is not inherited or done by " +
                    pos[0].Fetch().mo.name);
            }

            var de = from.FindMethod(name);
            if (de != null) {
                return de.info.SetupCall(th, de.outer, de.ip6,
                            pos, nam, false, de);
            } else {
                return Kernel.Die(th, "Unable to resolve method " + name +
                        " via " + from.name);
            }
        }
    }

    public static byte[] encode(string inp, string enc) {
        return Encoding.GetEncoding(enc).GetBytes(inp);
    }

    public static string decode(byte[] inp, string enc) {
        return Encoding.GetEncoding(enc).GetString(inp);
    }

    public static int blob_len(byte[] inp) {
        return inp.Length;
    }

    public static Socket socket_new(int family, int type, int proto) {
        return new Socket( (AddressFamily)family, (SocketType)type,
                (ProtocolType)proto );
    }

    public static byte[] socket_read(Socket from, int ct) {
        byte[] buf = new byte[ct];
        int rct = from.Receive(buf);
        Array.Resize(ref buf, rct);
        return buf;
    }

    public static void socket_write(Socket sock, byte[] data) {
        sock.Send(data);
    }

    public static void socket_close(Socket sock) {
        sock.Shutdown( SocketShutdown.Both ); // XXX perhaps should be separate
        sock.Close();
    }

    public static void socket_connect(Socket sock, string host, int port) {
        sock.Connect(host, port);
    }

    public static void socket_bind(Socket sock, string host, int port) {
        System.Net.IPAddress addr;
        if (host.Length == 0 || !System.Net.IPAddress.TryParse (host, out addr))
            addr = System.Net.Dns.GetHostEntry (host).AddressList[0];

        sock.Bind(new System.Net.IPEndPoint(addr, port));
    }

    public static void socket_listen(Socket sock, int log) {
        sock.Listen(log);
    }

    public static Socket socket_accept(Socket sock) {
        return sock.Accept();
    }

    [ImplicitConsts] public static Variable newarray(Constants c) {
        return c.setting.CreateArray();
    }

    [ImplicitConsts] public static Variable newhash(Constants c) {
        return c.setting.CreateHash();
    }

    public static Variable MakeJunction(Constants c, int type, Variable[] args) {
        return c.setting.MakeJunction(type, args);
    }

    [ImplicitConsts] public static Variable concat(Constants c, params Variable[] bits) {
        string[] sbits = new string[bits.Length];
        for (int i = 0; i < bits.Length; i++)
            sbits[i] = bits[i].Fetch().mo.mro_raw_Str.Get(bits[i]);
        return c.setting.MakeStr(string.Concat(sbits));
    }

    [ImplicitConsts] public static Variable list_join(Constants c, Variable sep, Variable lst) {
        string ssep = sep.Fetch().mo.mro_raw_Str.Get(sep);
        return c.setting.MakeStr(string.Join(ssep, UnboxLoS(lst)));
    }
}
