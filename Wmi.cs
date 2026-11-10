using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XenoSuite
{
    /// <summary>Thin helpers over the Storage WMI namespace (root\Microsoft\Windows\Storage).</summary>
    static class Wmi
    {
        public const string StorageNs = @"root\Microsoft\Windows\Storage";
        public const string Cimv2 = @"root\cimv2";

        public static IEnumerable<ManagementObject> Query(string ns, string wql)
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql));
            foreach (ManagementObject o in s.Get()) yield return o;
        }

        public static ManagementObject One(string ns, string wql) => Query(ns, wql).FirstOrDefault();

        public static T Get<T>(ManagementBaseObject o, string prop, T def = default)
        {
            try { var v = o[prop]; if (v == null) return def; return (T)Convert.ChangeType(v, typeof(T)); }
            catch { return def; }
        }

        public static object Val(ManagementBaseObject o, string prop) { try { return o[prop]; } catch { return null; } }

        public static ulong U(ManagementBaseObject o, string prop) { try { var v = o[prop]; return v == null ? 0UL : Convert.ToUInt64(v); } catch { return 0UL; } }
        public static uint Ui(ManagementBaseObject o, string prop) { try { var v = o[prop]; return v == null ? 0U : Convert.ToUInt32(v); } catch { return 0U; } }
        public static string Str(ManagementBaseObject o, string prop) { try { return o[prop]?.ToString(); } catch { return null; } }
        public static bool Bool(ManagementBaseObject o, string prop) { try { return o[prop] != null && Convert.ToBoolean(o[prop]); } catch { return false; } }

        /// <summary>Invoke a WMI method and translate the Storage ReturnValue into a readable error.</summary>
        public static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject> setParams = null)
        {
            var inp = target.GetMethodParameters(method);
            setParams?.Invoke(inp);
            var outp = target.InvokeMethod(method, inp, null);
            if (outp == null || Val(outp, "ReturnValue") == null)
                throw new InvalidOperationException(method + " did not return an operation status.");
            uint rv = Convert.ToUInt32(Val(outp, "ReturnValue"));
            if (rv != 0)
            {
                string ext = Str(outp, "ExtendedStatus");
                throw new InvalidOperationException($"{method} failed: {StorageError(rv)}" + (string.IsNullOrEmpty(ext) ? "" : $" ({ext})"));
            }
            return outp;
        }

        // MSFT_StorageObject return codes (subset, common ones)
        public static string StorageError(uint code) => code switch
        {
            0 => "Success",
            1 => "Not Supported",
            2 => "Unspecified Error",
            3 => "Timeout",
            4 => "Failed",
            5 => "Invalid Parameter",
            40001 => "Access denied",
            40002 => "There are not enough resources",
            42002 => "The disk is not initialized",
            42003 => "The disk is read-only — clear the read-only flag first",
            42004 => "The disk is offline — bring it online first",
            43001 => "Access path is already in use",
            44003 => "Size not supported — pick a size within the allowed min/max",
            44101 => "Cannot shrink — not enough free space at the end of the volume",
            49001 => "The requested size is below the minimum",
            _ => $"Storage error {code}",
        };
    }
}
