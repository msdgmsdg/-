using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Drawing;
using System.IO;
using System.Threading;

namespace DesktopIconToggle
{
    static class Program
    {
        static Mutex single;

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            bool created;
            single = new Mutex(true, "DesktopIconToggle_SingleMutex_7f3a91c2", out created);
            if (!created) return 0;

            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
            return 0;
        }

        static int SelfTest()
        {
            try
            {
                IntPtr lv = Desktop.FindDesktopListView();
                string report = "ListView=" + lv + " Visible=" + (lv != IntPtr.Zero && Native.IsWindowVisible(lv));
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "desktop-toggle-selftest.txt"), report);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "desktop-toggle-selftest.txt"), "ERROR: " + ex);
                return 1;
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miToggle, miAuto;
        IntPtr hook = IntPtr.Zero;
        Native.LowLevelMouseProc hookProc;

        public TrayApp()
        {
            hookProc = HookCallback;
            hook = Native.SetWindowsHookEx(14, hookProc, Native.GetModuleHandle(null), 0);

            menu = new ContextMenuStrip();
            miToggle = new ToolStripMenuItem("隐藏桌面图标", null, (s, e) => Toggle());
            miAuto = new ToolStripMenuItem("开机自动启动");
            miAuto.CheckOnClick = true;
            miAuto.CheckedChanged += (s, e) => AutoStart.SetEnabled(miAuto.Checked);
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出", null, (s, e) => Exit());
            menu.Items.Add(miToggle);
            menu.Items.Add(miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            menu.Opening += (s, e) => RefreshMenu();

            tray = new NotifyIcon();
            tray.Icon = GetDesktopIcon();
            tray.Text = "双击桌面空白处：显示/隐藏图标";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Toggle();
        }

        void RefreshMenu()
        {
            IntPtr lv = Desktop.FindDesktopListView();
            bool visible = lv != IntPtr.Zero && Native.IsWindowVisible(lv);
            miToggle.Text = visible ? "隐藏桌面图标" : "显示桌面图标";
            miAuto.Checked = AutoStart.IsEnabled();
        }

        Native.POINT lastDownPt;
        uint lastDownTime;
        bool hasLastDown;

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == 0)
            {
                int msg = (int)wParam;
                if (msg == 0x0201 || msg == 0x0203) // WM_LBUTTONDOWN 或 WM_LBUTTONDBLCLK
                {
                    try
                    {
                        Native.MSLLHOOKSTRUCT data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        uint now = (uint)Environment.TickCount;
                        bool isDouble;
                        if (msg == 0x0203)
                        {
                            isDouble = true; // 系统已确认的双击
                        }
                        else if (hasLastDown)
                        {
                            long dt = (long)now - lastDownTime;
                            int dx = Math.Abs(data.pt.x - lastDownPt.x);
                            int dy = Math.Abs(data.pt.y - lastDownPt.y);
                            isDouble = dt >= 0 && dt <= Native.GetDoubleClickTime()
                                && dx <= Native.GetSystemMetrics(36) && dy <= Native.GetSystemMetrics(37);
                        }
                        else isDouble = false;

                        lastDownPt = data.pt;
                        lastDownTime = now;
                        hasLastDown = true;

                        if (isDouble)
                            HandleDoubleClick(data.pt);
                    }
                    catch { }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void HandleDoubleClick(Native.POINT pt)
        {
            IntPtr lv = Desktop.FindDesktopListView();
            if (lv == IntPtr.Zero) return;
            IntPtr hwnd = Native.WindowFromPoint(pt);

            if (Native.IsWindowVisible(lv))
            {
                if (hwnd != lv) return;
                if (!Desktop.IsPointOnBlank(lv, pt)) return;
                Native.ShowWindow(lv, 0); // SW_HIDE
            }
            else
            {
                if (Desktop.IsPointOnDesktopContainer(hwnd))
                    Native.ShowWindow(lv, 5); // SW_SHOW
            }
        }

        void Toggle()
        {
            IntPtr lv = Desktop.FindDesktopListView();
            if (lv == IntPtr.Zero) return;
            Native.ShowWindow(lv, Native.IsWindowVisible(lv) ? 0 : 5);
        }

        void Exit()
        {
            tray.Visible = false;
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && tray != null) tray.Dispose();
            base.Dispose(disposing);
        }

        static Icon GetDesktopIcon()
        {
            try
            {
                IntPtr pidl;
                if (Native.SHGetSpecialFolderLocation(IntPtr.Zero, 0, out pidl) == 0)
                {
                    Native.SHFILEINFO sfi = new Native.SHFILEINFO();
                    IntPtr res = Native.SHGetFileInfo(pidl, 0, ref sfi, (uint)Marshal.SizeOf(sfi), 0x00000100 | 0x00000001);
                    Native.CoTaskMemFree(pidl);
                    if (res != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                        return Icon.FromHandle(sfi.hIcon);
                }
            }
            catch { }
            return SystemIcons.Application;
        }
    }

    static class AutoStart
    {
        const string NAME = "DesktopIconToggle";
        const string RUN = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN, false))
                return k != null && k.GetValue(NAME) != null;
        }

        public static void SetEnabled(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN, true))
            {
                if (k == null) return;
                if (on) k.SetValue(NAME, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(NAME, false);
            }
        }
    }

    static class Desktop
    {
        public static IntPtr FindDesktopListView()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr def = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr lv = Native.FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero) return lv;

            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, lp) =>
            {
                if (ClassOf(h) == "WorkerW")
                {
                    IntPtr d = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (d != IntPtr.Zero)
                    {
                        IntPtr l = Native.FindWindowEx(d, IntPtr.Zero, "SysListView32", null);
                        if (l != IntPtr.Zero) { found = l; return false; }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsPointOnBlank(IntPtr lv, Native.POINT screenPt)
        {
            Native.POINT c = screenPt;
            if (!Native.ScreenToClient(lv, ref c)) return false;

            uint pid;
            Native.GetWindowThreadProcessId(lv, out pid);
            IntPtr hp = Native.OpenProcess(0x0438, false, pid);
            if (hp == IntPtr.Zero) return false;
            IntPtr remote = Native.VirtualAllocEx(hp, IntPtr.Zero, 20, 0x3000, 4);
            if (remote == IntPtr.Zero) { Native.CloseHandle(hp); return false; }
            try
            {
                byte[] buf = new byte[20];
                BitConverter.GetBytes(c.x).CopyTo(buf, 0);
                BitConverter.GetBytes(c.y).CopyTo(buf, 4);
                IntPtr n;
                if (!Native.WriteProcessMemory(hp, remote, buf, 20, out n)) return false;
                Native.SendMessage(lv, 0x1012u, IntPtr.Zero, remote); // LVM_HITTEST
                byte[] rb = new byte[20];
                if (!Native.ReadProcessMemory(hp, remote, rb, 20, out n)) return false;
                return BitConverter.ToInt32(rb, 12) < 0;
            }
            finally
            {
                Native.VirtualFreeEx(hp, remote, IntPtr.Zero, 0x8000);
                Native.CloseHandle(hp);
            }
        }

        public static bool IsPointOnDesktopContainer(IntPtr hwnd)
        {
            for (IntPtr h = hwnd; h != IntPtr.Zero; h = Native.GetParent(h))
            {
                string s = ClassOf(h);
                if (s == "SHELLDLL_DefView" || s == "Progman") return true;
                if (s == "WorkerW" && Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return true;
            }
            return false;
        }

        static string ClassOf(IntPtr h)
        {
            StringBuilder cn = new StringBuilder(256);
            Native.GetClassName(h, cn, cn.Capacity);
            return cn.ToString();
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr hWnd1, IntPtr hWnd2, string lpszClass, string lpszWindow);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);
        [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("shell32.dll")] public static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
    }
}
