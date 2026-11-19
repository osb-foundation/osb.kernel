# FS-PRIMITIVES — path-based file syscalls (design, B2a)

Replace 26h/27h/28h/29h/61h/62h by path-based primitives. Each returns 0/n
or a negative code from the error table; no printing, no command-line parsing.
Permission checks in the kernel with the task's effective uid.

## Primitives
- MKDIR path -> 0/-errno
- RMDIR path -> 0/-errno
- UNLINK path -> 0/-errno (hard delete)
- RENAME old new -> 0/-errno
- LINK target linkname (hard/symbolic) -> 0/-errno
- READLINK path buf len -> n/-errno
- OPEN path flags -> handle/-errno
- READ handle buf len -> n/-errno
- WRITE handle buf len -> n/-errno
- LSEEK handle off whence -> pos/-errno
- CLOSE handle -> 0/-errno
- STAT path buf -> 0/-errno (metadata only, no read check)

## Permission rules
- create/remove: parent write+execute
- open-to-read: read on file (STAT no read check)
- chmod/chown: owner or root
- symlinks: READLINK on link; OPEN follows unless O_NOFOLLOW

## Path length cap: 256 bytes (incl. NUL). Behavior for symlinks: OPEN follows by default.

## Trash policy moves to shell (A+B): DEL = rename into $HOME\TRASH with .ORIG record.

## Kernel Shell/ folder renamed by responsibility (Fs/, Security/Users.cs, Processes/Environment.cs); dead code deleted.

## ABI version bump: one version for whole group. Test plan: ring-3 test programs per syscall incl. non-root denial, not found, not empty, already exists, invalid names, long paths, bad pointers.
