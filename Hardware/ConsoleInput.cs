// Cooked console input for ring-3 programs (GETCHAR, AH=6Eh).
//
// Merges the two hardware input sources into one byte stream:
//   * serial (COM1, IRQ4) bytes are already ASCII -> pushed as-is
//   * PS/2 (IRQ1) scancodes are translated to ASCII here (shift/extended)
// Ctrl+C is delivered as a one-shot -2 (once per press) instead of a byte, so
// a ring-3 line reader can abort its line exactly once. The legacy in-kernel
// shell keeps its own ConsoleControl path; this class is only for ring 3.

using Osb.Kernel.Memory;

namespace Osb.Kernel.Hardware;

internal static unsafe class ConsoleInput
{
    // Cooked byte ring (ASCII from serial or translated PS/2).
    private unsafe struct ByteRing
    {
        public fixed byte data[256];
        public int head;
        public int tail;
    }

    private static ByteRing _ring;
    private static bool _shift;       // PS/2 shift currently held
    private static bool _ctrl;        // PS/2 ctrl currently held
    private static bool _extended;    // PS/2 0xE0 prefix seen
    private static int _ctrlC;        // pending Ctrl+C presses (one-shot -> -2)

    // Serial path: bytes are already ASCII (control bytes handled by the IRQ).
    public static void PushSerial(byte c)
    {
        int next = _ring.tail + 1;
        if (next >= 256) next = 0;
        if (next != _ring.head)
        {
            _ring.data[_ring.tail] = c;
            _ring.tail = next;
        }
    }

    // PS/2 path: a raw scancode from IRQ1. Translates to ASCII and pushes.
    // Tracks shift/ctrl so Ctrl+C is caught here (Keyboard consumes it for the
    // legacy shell; ConsoleInput sees the raw stream and handles it for ring 3).
    public static void PushScancode(byte sc)
    {
        if (sc == 0x2A || sc == 0x36) { _shift = true; return; }   // shift down
        if (sc == 0xAA || sc == 0xB6) { _shift = false; return; }  // shift up
        if (sc == 0x1D) { _ctrl = true; return; }                  // ctrl down
        if (sc == 0x9D) { _ctrl = false; return; }                 // ctrl up
        if (sc == 0xE0) { _extended = true; return; }              // extended prefix

        if (_extended)
        {
            _extended = false;
            // S1: extended (0xE0) keys are synthesized to the SAME ANSI
            // sequences a serial terminal delivers, so both input sources are
            // identical for the line editor (no shell behavior for them yet).
            switch (sc)
            {
                case 0x48: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'A'); return; // Up
                case 0x50: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'B'); return; // Down
                case 0x4D: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'C'); return; // Right
                case 0x4B: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'D'); return; // Left
                case 0x47: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'H'); return; // Home
                case 0x4F: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'F'); return; // End
                case 0x53: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'3'); PushSerial((byte)'~'); return; // Delete
                default: return;   // other extended keys: not cooked yet
            }
        }

        if (sc == 0x2E && _ctrl) { RequestCtrlC(); return; }       // Ctrl+C (one-shot)
        if (sc == 0x3E && _ctrl) return;                           // Ctrl+F4: not cooked

        if ((sc & 0x80) != 0) return;   // ignore break codes

        if (sc == 0x1C) { PushSerial((byte)'\r'); return; }   // Enter
        if (sc == 0x0E) { PushSerial((byte)'\b'); return; }   // Backspace
        if (sc == 0x0F) { PushSerial((byte)'\t'); return; }   // Tab

        char c = Keyboard.MapScancode(sc, _shift);
        if (c != 0) PushSerial((byte)c);
    }

    // A Ctrl+C was pressed (from serial 0x03 or PS/2 Ctrl+C). Counted so the
    // GETCHAR caller sees -2 exactly once per press.
    public static void RequestCtrlC()
    {
        if (_ctrlC < 0x7FFFFFFF) _ctrlC++;
    }

    // S2: drop any buffered cooked bytes and pending Ctrl+C. Called when a new
    // foreground program is spawned so stale input (or a -2 from a previous
    // line) does not leak into the next program.
    public static void Flush()
    {
        _ring.head = 0;
        _ring.tail = 0;
        _ctrlC = 0;
        _shift = false;
        _ctrl = false;
        _extended = false;
    }

    // GETCHAR result: next cooked byte (0..255), -1 when empty, -2 for a
    // pending Ctrl+C (consumed once). Non-blocking. Buffered bytes are drained
    // before the Ctrl+C so a line typed before Ctrl+C is consumed (and then
    // discarded by the aborting reader), not leaked into the next line.
    public static int Pop()
    {
        if (_ring.head != _ring.tail)
        {
            byte c = _ring.data[_ring.head];
            int next = _ring.head + 1;
            if (next >= 256) next = 0;
            _ring.head = next;
            return c;
        }
        if (_ctrlC > 0) { _ctrlC--; return -2; }
        return -1;
    }
}