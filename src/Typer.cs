using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyTyper
{
    internal struct TypeResult
    {
        public int Typed;
        public int UnicodeFallbacks;
        public int Failed;
        public bool Cancelled;
    }

    internal static class Typer
    {
        public static TypeResult TypeText(string text, int delayMs, bool unicodeMode, Func<bool> shouldStop, Action<int, int> progress)
        {
            TypeResult result = new TypeResult();
            IntPtr layout = GetTargetLayout();
            int i = 0;

            while (i < text.Length)
            {
                if (shouldStop != null && shouldStop())
                {
                    result.Cancelled = true;
                    break;
                }

                char c = text[i];

                if (c == '\r')
                {
                    bool newline = true;
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    if (newline)
                    {
                        if (!SendVirtualKey(Native.VK_RETURN, false, false, false))
                        {
                            result.Failed++;
                            break;
                        }
                        result.Typed++;
                    }
                    i++;
                    ReportProgress(progress, i, text.Length, result.Typed);
                    if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
                    continue;
                }

                if (c == '\n')
                {
                    if (!SendVirtualKey(Native.VK_RETURN, false, false, false))
                    {
                        result.Failed++;
                        break;
                    }
                    result.Typed++;
                    i++;
                    ReportProgress(progress, i, text.Length, result.Typed);
                    if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
                    continue;
                }

                if (c == '\t')
                {
                    if (!SendVirtualKey(Native.VK_TAB, false, false, false))
                    {
                        result.Failed++;
                        break;
                    }
                    result.Typed++;
                    i++;
                    ReportProgress(progress, i, text.Length, result.Typed);
                    if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
                    continue;
                }

                if (unicodeMode)
                {
                    if (SendUnicodeChar(text, ref i))
                    {
                        result.Typed++;
                        result.UnicodeFallbacks++;
                    }
                    else
                    {
                        result.Failed++;
                        break;
                    }
                    ReportProgress(progress, i, text.Length, result.Typed);
                    if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
                    continue;
                }

                short scan = Native.VkKeyScanExW(c, layout);
                if (scan == -1)
                {
                    if (SendUnicodeChar(text, ref i))
                    {
                        result.Typed++;
                        result.UnicodeFallbacks++;
                    }
                    else
                    {
                        result.Failed++;
                        break;
                    }
                    ReportProgress(progress, i, text.Length, result.Typed);
                    if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
                    continue;
                }

                int vk = scan & 0xFF;
                int state = (scan >> 8) & 0xFF;
                bool shift = (state & 1) != 0;
                bool ctrl = (state & 2) != 0;
                bool alt = (state & 4) != 0;

                if (!SendVirtualKey((ushort)vk, shift, ctrl, alt))
                {
                    result.Failed++;
                    break;
                }

                result.Typed++;
                i++;
                ReportProgress(progress, i, text.Length, result.Typed);
                if (!Pause(delayMs, shouldStop)) { result.Cancelled = true; break; }
            }

            if (progress != null)
            {
                progress(i, text.Length);
            }
            return result;
        }

        private static void ReportProgress(Action<int, int> progress, int current, int total, int typed)
        {
            if (progress != null && (typed % 10 == 0 || current >= total))
            {
                progress(current, total);
            }
        }

        public static bool SendCtrlV()
        {
            return SendVirtualKey(Native.VK_V, false, true, false);
        }

        public static bool SendCtrlEnd()
        {
            return SendVirtualKey(Native.VK_END, false, true, false);
        }

        private static bool Pause(int delayMs, Func<bool> shouldStop)
        {
            int remaining = delayMs > 0 ? delayMs : 1;
            while (remaining > 0)
            {
                if (shouldStop != null && shouldStop())
                {
                    return false;
                }

                int slice = Math.Min(remaining, 10);
                Thread.Sleep(slice);
                remaining -= slice;
            }

            return shouldStop == null || !shouldStop();
        }

        private static IntPtr GetTargetLayout()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            uint pid;
            IntPtr thread = Native.GetWindowThreadProcessId(hwnd, out pid);
            IntPtr layout = IntPtr.Zero;

            if (thread != IntPtr.Zero)
            {
                layout = Native.GetKeyboardLayout((uint)thread.ToInt64());
            }

            if (layout == IntPtr.Zero)
            {
                layout = Native.GetKeyboardLayout(0);
            }

            return layout;
        }

        private static bool SendVirtualKey(ushort vk, bool shift, bool ctrl, bool alt)
        {
            List<Native.INPUT> events = new List<Native.INPUT>(8);

            if (ctrl)
            {
                events.Add(KeyInput(Native.VK_CONTROL, false));
            }
            if (shift)
            {
                events.Add(KeyInput(Native.VK_SHIFT, false));
            }
            if (alt)
            {
                events.Add(KeyInput(Native.VK_MENU, false));
            }

            events.Add(KeyInput(vk, false));
            events.Add(KeyInput(vk, true));

            if (alt)
            {
                events.Add(KeyInput(Native.VK_MENU, true));
            }
            if (shift)
            {
                events.Add(KeyInput(Native.VK_SHIFT, true));
            }
            if (ctrl)
            {
                events.Add(KeyInput(Native.VK_CONTROL, true));
            }

            return SendAll(events);
        }

        private static bool SendUnicodeChar(string text, ref int index)
        {
            char c = text[index];

            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                char low = text[index + 1];
                Native.INPUT[] events = new Native.INPUT[4];
                events[0] = UnicodeInput(c, false);
                events[1] = UnicodeInput(low, false);
                events[2] = UnicodeInput(c, true);
                events[3] = UnicodeInput(low, true);

                index += 2;
                return Native.SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Native.INPUT))) == events.Length;
            }

            Native.INPUT[] pair = new Native.INPUT[2];
            pair[0] = UnicodeInput(c, false);
            pair[1] = UnicodeInput(c, true);
            index++;
            return Native.SendInput(2, pair, Marshal.SizeOf(typeof(Native.INPUT))) == 2;
        }

        private static bool SendAll(List<Native.INPUT> events)
        {
            if (events.Count == 0)
            {
                return true;
            }

            Native.INPUT[] arr = events.ToArray();
            uint sent = Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
            return sent == arr.Length;
        }

        private static Native.INPUT KeyInput(ushort vk, bool keyUp)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = 0;
            input.U.ki.dwFlags = keyUp ? Native.KEYEVENTF_KEYUP : 0;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        private static Native.INPUT UnicodeInput(char c, bool keyUp)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = (ushort)c;
            input.U.ki.dwFlags = Native.KEYEVENTF_UNICODE | (keyUp ? Native.KEYEVENTF_KEYUP : 0);
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }
    }
}
