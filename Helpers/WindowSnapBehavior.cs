using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XColumn.Helpers
{
    /// <summary>
    /// ウィンドウのスナップ（吸着）および連動移動・Zオーダー制御を管理するビヘイビア。
    /// MainWindow から Win32 API 呼び出しと WndProc フックを完全に分離します。
    /// </summary>
    public class WindowSnapBehavior
    {
        private const int SnapDistance = 15;
        private const string SNAP_PROP_NAME = "XColumn_SnapEnabled";

        private readonly Window _window;
        private HwndSource? _hwndSource;
        private bool _isEnabled = true;

        private List<IntPtr> _connectedWindows = new();
        private List<IntPtr> _cachedSnapTargets = new();
        private RECT _virtualScreenBounds;
        private POINT _lastWindowPos;
        private bool _isDragging = false;

        public WindowSnapBehavior(Window window)
        {
            _window = window;
        }

        public void Attach(bool isEnabled)
        {
            _isEnabled = isEnabled;
            if (_hwndSource == null)
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    _hwndSource = HwndSource.FromHwnd(hwnd);
                    _hwndSource?.AddHook(WndProc);
                    UpdateSnapProperty();
                }
            }
        }

        public void Detach()
        {
            if (_hwndSource != null)
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    RemoveProp(hwnd, SNAP_PROP_NAME);
                }

                // バグ#5修正: _hwndSource.Dispose() は絶対に呼ばず、RemoveHook のみを行う
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        }

        public void SetEnabled(bool isEnabled)
        {
            _isEnabled = isEnabled;
            UpdateSnapProperty();
        }

        public void UpdateSnapProperty()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (_isEnabled)
            {
                SetProp(hwnd, SNAP_PROP_NAME, (IntPtr)1);
            }
            else
            {
                RemoveProp(hwnd, SNAP_PROP_NAME);
            }
        }

        public void BringSnappedWindowsToFront()
        {
            if (!_isEnabled) return;
            try
            {
                var myHwnd = new WindowInteropHelper(_window).Handle;
                if (myHwnd == IntPtr.Zero) return;
                if (!GetWindowRect(myHwnd, out RECT myRect)) return;

                var targets = FindSnappableWindows(myHwnd);
                IntPtr previousHwnd = myHwnd;

                foreach (var targetHwnd in targets)
                {
                    if (!GetWindowRect(targetHwnd, out RECT targetRect)) continue;
                    if (AreRectsTouching(myRect, targetRect, 5))
                    {
                        SetWindowPos(targetHwnd, previousHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        previousHwnd = targetHwnd;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowSnapBehavior] BringSnappedWindowsToFront error: {ex.Message}");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_isEnabled) return IntPtr.Zero;

            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    _isDragging = true;
                    _cachedSnapTargets = FindSnappableWindows(hwnd);
                    UpdateVirtualScreenBounds();
                    GetCursorPos(out _lastWindowPos);
                    if (GetWindowRect(hwnd, out RECT initialRect))
                    {
                        _connectedWindows = FindConnectedCluster(hwnd, initialRect, _cachedSnapTargets);
                    }
                    break;

                case WM_MOVING:
                    if (_isDragging)
                    {
                        RECT newRect = Marshal.PtrToStructure<RECT>(lParam);
                        SnapWindow(hwnd, ref newRect);
                        Marshal.StructureToPtr(newRect, lParam, true);

                        int dx = newRect.Left - (int)_window.Left;
                        int dy = newRect.Top - (int)_window.Top;

                        if (dx != 0 || dy != 0)
                        {
                            foreach (var connectedHwnd in _connectedWindows)
                            {
                                if (GetWindowRect(connectedHwnd, out RECT targetRect))
                                {
                                    SetWindowPos(connectedHwnd, IntPtr.Zero,
                                        targetRect.Left + dx, targetRect.Top + dy, 0, 0,
                                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                                }
                            }
                        }
                    }
                    break;

                case WM_EXITSIZEMOVE:
                    _isDragging = false;
                    _cachedSnapTargets.Clear();
                    _connectedWindows.Clear();
                    break;
            }

            return IntPtr.Zero;
        }

        private void SnapWindow(IntPtr myHwnd, ref RECT rect)
        {
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            // 仮想スクリーン（モニター外枠）吸着
            if (Math.Abs(rect.Left - _virtualScreenBounds.Left) <= SnapDistance)
            {
                rect.Left = _virtualScreenBounds.Left;
                rect.Right = rect.Left + width;
            }
            if (Math.Abs(rect.Right - _virtualScreenBounds.Right) <= SnapDistance)
            {
                rect.Right = _virtualScreenBounds.Right;
                rect.Left = rect.Right - width;
            }
            if (Math.Abs(rect.Top - _virtualScreenBounds.Top) <= SnapDistance)
            {
                rect.Top = _virtualScreenBounds.Top;
                rect.Bottom = rect.Top + height;
            }
            if (Math.Abs(rect.Bottom - _virtualScreenBounds.Bottom) <= SnapDistance)
            {
                rect.Bottom = _virtualScreenBounds.Bottom;
                rect.Top = rect.Bottom - height;
            }

            // 他ウィンドウとの吸着
            foreach (var targetHwnd in _cachedSnapTargets)
            {
                if (_connectedWindows.Contains(targetHwnd)) continue;
                if (!GetWindowRect(targetHwnd, out RECT targetRect)) continue;

                if (rect.Bottom > targetRect.Top - SnapDistance && rect.Top < targetRect.Bottom + SnapDistance)
                {
                    if (Math.Abs(rect.Left - targetRect.Right) <= SnapDistance)
                    {
                        rect.Left = targetRect.Right;
                        rect.Right = rect.Left + width;
                    }
                    else if (Math.Abs(rect.Right - targetRect.Left) <= SnapDistance)
                    {
                        rect.Right = targetRect.Left;
                        rect.Left = rect.Right - width;
                    }
                }

                if (rect.Right > targetRect.Left - SnapDistance && rect.Left < targetRect.Right + SnapDistance)
                {
                    if (Math.Abs(rect.Top - targetRect.Bottom) <= SnapDistance)
                    {
                        rect.Top = targetRect.Bottom;
                        rect.Bottom = rect.Top + height;
                    }
                    else if (Math.Abs(rect.Bottom - targetRect.Top) <= SnapDistance)
                    {
                        rect.Bottom = targetRect.Top;
                        rect.Top = rect.Bottom - height;
                    }
                }
            }
        }

        private List<IntPtr> FindConnectedCluster(IntPtr startHwnd, RECT startRect, List<IntPtr> allTargets)
        {
            var cluster = new List<IntPtr>();
            var queue = new Queue<(IntPtr hwnd, RECT rect)>();
            queue.Enqueue((startHwnd, startRect));

            var visited = new HashSet<IntPtr> { startHwnd };

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var candidate in allTargets)
                {
                    if (visited.Contains(candidate)) continue;
                    if (GetWindowRect(candidate, out RECT candidateRect))
                    {
                        if (AreRectsTouching(current.rect, candidateRect, 5))
                        {
                            visited.Add(candidate);
                            cluster.Add(candidate);
                            queue.Enqueue((candidate, candidateRect));
                        }
                    }
                }
            }

            return cluster;
        }

        private static bool AreRectsTouching(RECT r1, RECT r2, int tolerance)
        {
            bool xOverlap = (r1.Left < r2.Right + tolerance) && (r1.Right > r2.Left - tolerance);
            bool yOverlap = (r1.Top < r2.Bottom + tolerance) && (r1.Bottom > r2.Top - tolerance);

            bool xTouching = Math.Abs(r1.Right - r2.Left) <= tolerance || Math.Abs(r1.Left - r2.Right) <= tolerance;
            bool yTouching = Math.Abs(r1.Bottom - r2.Top) <= tolerance || Math.Abs(r1.Top - r2.Bottom) <= tolerance;

            return (xOverlap && yTouching) || (yOverlap && xTouching);
        }

        private List<IntPtr> FindSnappableWindows(IntPtr myHwnd)
        {
            var result = new List<IntPtr>();
            EnumWindows((hwnd, lParam) =>
            {
                if (hwnd != myHwnd && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    if (GetProp(hwnd, SNAP_PROP_NAME) != IntPtr.Zero)
                    {
                        result.Add(hwnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private void UpdateVirtualScreenBounds()
        {
            _virtualScreenBounds = new RECT
            {
                Left = (int)SystemParameters.VirtualScreenLeft,
                Top = (int)SystemParameters.VirtualScreenTop,
                Right = (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth),
                Bottom = (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            };
        }

        #region Win32 API
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_MOVING = 0x0216;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);
        [DllImport("user32.dll")] private static extern IntPtr GetProp(IntPtr hWnd, string lpString);
        [DllImport("user32.dll")] private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        #endregion
    }
}
