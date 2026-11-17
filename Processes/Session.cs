// Kernel-owned session state (1c, I1/I3). The session leader (the boot program,
// i.e. the shell) carries the session's uid/gid/user; LOGIN/LOGOUT/SUDO/END_SUDO/
// WHOAMI are the only paths that read or mutate it. The per-task fields
// (sessionId, uid, realUid, gid) live in the scheduler task table; this file
// holds the session-wide bookkeeping and the syscall-facing operations.
//
// Session rules (MIGRATION §3.4/§3.7, decisions A1/A3/A4/D3):
//   * The leader starts with NO uid (uid = -1); it must LOGIN before it can do
//     anything privileged. root is passwordless (Users table), so LOGIN root
//     succeeds with an empty password, exactly as legacy.
//   * LOGIN/LOGOUT are accepted ONLY from the session leader (D3).
//   * Every spawned task inherits sessionId/uid/realUid/gid from its parent
//     (SeedSlot); a child spawned while the parent is sudo-elevated inherits
//     uid 0 (I3).
//   * LOGOUT kills every task in the session except the leader and kernel
//     tasks (session 0), then clears the leader's uid (E2).
//   * SUDO elevates the CALLING task's effective uid to 0 iff the session user
//     is in SUDOERS; END_SUDO restores it. No other path raises privileges.

using Osb.Kernel.Memory;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    // The session leader (the boot program). Its slot identifies the task that
    // owns the console session; LOGIN/LOGOUT are accepted only from it (D3).
    private static int _leaderSlot = -1;
    private static int _sessionId = 0;    // 0 = no user session yet

    // Called once at boot, after the boot program (the shell) is spawned. Turns
    // that task into the session leader of a fresh session that starts with NO
    // uid, so it must LOGIN before doing anything privileged.
    public static void BeginFirstSession(int pid)
    {
        if (!TryFindPid(pid, out int slot)) return;
        _leaderSlot = slot;
        _sessionId = 1;
        _tasks.sessionId[slot] = _sessionId;
        _tasks.uid[slot] = -1;            // not logged in
        _tasks.realUid[slot] = -1;
        _tasks.gid[slot] = -1;
    }

    // The calling task is the session leader?
    private static bool IsLeader(int slot) { return slot == _leaderSlot && _leaderSlot >= 0; }

    // 65h LOGIN(name, nameLen, pass, passLen) -> 0 ok, -3 any auth failure.
    // Unknown user and wrong password return the SAME code (-3). On any failure
    // nothing is changed.
    public static int Login(byte* name, int nameLen, byte* pass, int passLen)
    {
        if (!IsLeader(_current)) return -3;

        // Q5: a LOGIN while a user is already logged in first performs the
        // LOGOUT teardown (kill session tasks except the leader and kernel
        // tasks, clear the uid), THEN authenticates. The legacy
        // UserCommands.Login just called SessionSetup and left the old
        // session's tasks running — a deliberate deviation (see
        // osb.shell/docs/DEVIATIONS.md and MIGRATION.md).
        if (_tasks.realUid[_leaderSlot] >= 0) TeardownSession();

        // Q3: reject an empty or over-long name outright; never truncate. A
        // name that does not fit Users.NameCap is not a valid account name.
        if (nameLen <= 0 || nameLen >= Users.NameCap) return -3;

        char* nbuf = stackalloc char[Users.NameCap];
        for (int k = 0; k < nameLen; k++) nbuf[k] = (char)name[k];
        int idx = Users.Find(nbuf, nameLen);
        if (idx < 0) return -3;                       // unknown user

        char* pbuf = stackalloc char[Users.PassCap];
        int pn = passLen < Users.PassCap ? passLen : Users.PassCap;
        for (int k = 0; k < pn; k++) pbuf[k] = (char)pass[k];
        if (Users.PassLen(idx) > 0 && !Users.PassMatches(idx, pbuf, passLen))
            return -3;                                // wrong password

        // Success: mutate the session (no failure path above touched it).
        int uid = Users.UidAt(idx);
        int slot = _leaderSlot;
        _tasks.realUid[slot] = uid;
        _tasks.uid[slot] = uid;
        _tasks.gid[slot] = uid;                       // gid = uid (no group db)
        SetSessionCwd(idx);
        return 0;
    }

    // 66h LOGOUT() -> 0 ok, -3 if the caller is not the session leader.
    // Kills every task in the session except the leader and kernel tasks
    // (session 0), clears the leader's uid, and returns so the shell prints a
    // fresh "login:".
    public static int Logout()
    {
        if (!IsLeader(_current)) return -3;
        TeardownSession();
        return 0;
    }

    // Q5/Q2: kill every task in the session except the leader and kernel tasks
    // (session 0), then clear the leader's uid. Shared by LOGOUT and a re-LOGIN
    // so the two paths cannot drift.
    private static void TeardownSession()
    {
        int sid = _sessionId;
        for (int i = 0; i < Count; i++)
        {
            if (i == _leaderSlot) continue;                       // leader survives
            if (_tasks.state[i] == Layout.TaskFree) continue;
            if (sid != 0 && _tasks.sessionId[i] == sid) Release(i); // session tasks only
        }
        _tasks.uid[_leaderSlot] = -1;
        _tasks.realUid[_leaderSlot] = -1;
        _tasks.gid[_leaderSlot] = -1;
    }

    // 67h SUDO() -> 0 ok, -3 if the session user is not in SUDOERS.
    // Elevates the CALLING task's effective uid to 0 (I3). Children spawned
    // while elevated inherit uid 0; END_SUDO restores it.
    public static int Sudo()
    {
        int uid = _tasks.realUid[_current];
        if (uid < 0) return -3;                       // not logged in
        if (!Users.IsSudoerByUid((uint)uid)) return -3;
        _tasks.uid[_current] = 0;                     // elevate
        return 0;
    }

    // 68h END_SUDO() -> 0. Restores the calling task's effective uid to the
    // session user's real uid.
    public static int EndSudo()
    {
        _tasks.uid[_current] = _tasks.realUid[_current];
        return 0;
    }

    // 69h WHOAMI(buf, cap) -> name length, -3 if not logged in.
    // Returns the session user's name (never password data).
    public static int WhoAmI(byte* buf, int cap)
    {
        int uid = _tasks.realUid[_current];
        if (uid < 0) return -3;
        char* nm = stackalloc char[Users.NameCap];
        if (!Users.NameOf((uint)uid, nm, out int len)) return -3;
        int n = len < cap ? len : cap;
        for (int k = 0; k < n; k++) buf[k] = (byte)nm[k];
        return n;
    }

    // LOGIN sets the session cwd to the user's home (canonical), or A:\ when
    // the home cannot be resolved — exactly the legacy SessionSetup behavior.
    private static void SetSessionCwd(int idx)
    {
        char* home = stackalloc char[Users.HomeCap];
        int hl = Users.HomeAt(idx, home);
        if (Ext4.Mount())
        {
            byte* canon = stackalloc byte[300];
            int clen = 0;
            uint ino;
            if (Ext4.ResolveCanonical(home, hl, out ino, canon, ref clen))
            {
                Cwd.Set(canon, clen);
                return;
            }
        }
        byte* root = stackalloc byte[4];
        root[0] = (byte)'A'; root[1] = (byte)':'; root[2] = (byte)'\\';
        Cwd.Set(root, 3);
    }
}