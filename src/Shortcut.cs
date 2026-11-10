// Shortcut.cs — 建带 AppUserModelID 的开始菜单快捷方式（Windows 通知的前置条件）
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PhoneAudioTray
{
    internal static class Shortcut
    {
        // ---- COM 定义 ----
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pointerValue;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
        private static readonly Guid PKEY_AppUserModel_ID_Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const int PKEY_AppUserModel_ID_Pid = 5;
        private const short VT_LPWSTR = 31;

        /// <summary>开始菜单路径：「开始菜单\程序\传声.lnk」</summary>
        public static string StartMenuLnk
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), "传声.lnk");
            }
        }

        /// <summary>确保存在一条带 AppUserModelID 的开始菜单快捷方式；已存在则跳过</summary>
        public static bool Ensure(string targetExe, string iconPath, string appId, string description)
        {
            try
            {
                var lnk = StartMenuLnk;
                if (File.Exists(lnk))
                {
                    // 已存在就不重复建，避免每次启动都动开始菜单
                    return true;
                }

                IShellLinkW link = (IShellLinkW)new CShellLink();
                link.SetPath(targetExe);
                link.SetWorkingDirectory(Path.GetDirectoryName(targetExe));
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);

                // 关键：给快捷方式打上 AppUserModelID
                var store = (IPropertyStore)link;
                var key = new PROPERTYKEY
                {
                    fmtid = PKEY_AppUserModel_ID_Fmtid,
                    pid = PKEY_AppUserModel_ID_Pid
                };
                var pv = new PROPVARIANT
                {
                    vt = VT_LPWSTR,
                    pointerValue = Marshal.StringToCoTaskMemUni(appId)
                };
                try
                {
                    store.SetValue(ref key, ref pv);
                    store.Commit();
                }
                finally
                {
                    if (pv.pointerValue != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.pointerValue);
                }

                var persist = (IPersistFile)link;
                persist.Save(lnk, true);
                Marshal.ReleaseComObject(persist);
                Marshal.ReleaseComObject(store);
                Marshal.ReleaseComObject(link);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
