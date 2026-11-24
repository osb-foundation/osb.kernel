# OSB — Architectural Philosophy & Design Manifesto

**Operating System Basic**

**Document Type:** Architectural Manifesto  
**Status:** Foundational Principles  
**Scope:** Entire OSB ecosystem  
**Version:** 1.0

---

# 1. Vision

OSB (Operating System Basic) is an independent 64-bit operating system designed around simplicity, modularity, and a coherent user experience.

OSB is not intended to be a DOS clone, a POSIX implementation, a Unix derivative, or a replacement for general-purpose operating systems such as Linux or Windows.

Instead, OSB combines selected ideas from different operating system paradigms to create an environment with its own identity.

Its philosophy can be summarized as:

- DOS-like simplicity in everyday interaction.
- Modular architecture with explicit subsystem boundaries.
- Modern 64-bit execution environment.
- Independent applications and user-mode services.
- A graphical experience inspired by the simplicity of classic desktop environments.
- A first-class programming language and application ecosystem.
- Deliberate technological choices instead of unnecessary duplication.

OSB should be technically sophisticated internally while remaining straightforward to understand and use.

**The system should be simple for its users, not necessarily simplistic in its implementation.**

# 2. Core Principles

## 2.1 Simplicity

Simplicity is the defining characteristic of OSB.

The system should avoid unnecessary abstractions, configuration complexity, competing implementations, and redundant functionality.

When a capability already exists, extend and reuse it whenever practical instead of introducing another mechanism that solves the same problem.

The default should be one clear, coherent way to accomplish a task.

Additional alternatives should exist when they provide a meaningful technical or practical benefit, not merely because they are possible.

Simplicity does not mean sacrificing correctness, reliability, extensibility, or architectural quality.

## 2.2 Modularity

Subsystems should have clear responsibilities and communicate through explicit interfaces.

Components should be replaceable or independently evolvable whenever practical.

The kernel must not become the default home for every system capability.

Drivers, filesystems, services, user interfaces, language runtimes, and applications should remain independent components whenever their responsibilities allow it.

Modularity should reduce coupling and simplify evolution, not create layers without meaningful purpose.

## 2.3 Deliberate Technology Choices

OSB should adopt proven technologies and ideas when they serve its goals.

Examples include:

- Existing filesystem implementations.
- Established storage and device protocols.
- Well-understood scheduling and memory-management concepts.
- Proven graphical rendering techniques.
- Familiar programming language concepts.

Using an existing technology does not compromise OSB's identity.

The identity of OSB comes from its architecture, interfaces, integration, and user experience, not from reinventing every component.

## 2.4 Independent Identity

OSB may borrow ideas from DOS, Unix, Linux, Windows, MINIX, and other systems.

However, it must not inherit their architectural assumptions automatically.

Every adopted concept must justify its existence within OSB.

Compatibility is a tool, not the primary design objective.

# 3. Kernel Philosophy

## 3.1 The Kernel Should Remain Focused

The kernel should provide fundamental mechanisms required to operate the system.

Its responsibilities include the essential foundations of:

- Process and thread management.
- Scheduling.
- Address spaces and memory management.
- Privilege boundaries.
- Interrupt and exception handling.
- System calls.
- Fundamental inter-process communication mechanisms.
- Other capabilities that genuinely require kernel privileges.

The kernel should not become a repository for higher-level system policies or application-specific functionality.

## 3.2 User-Mode First

System components should execute in Ring3 whenever technically and practically possible.

This applies especially to:

- Device drivers.
- System daemons.
- Filesystem implementations.
- Graphical services.
- Other system-level services.

Kernel-mode implementations are acceptable when required by hardware access, performance constraints, architectural limitations, or fundamental operating system responsibilities.

However, kernel-mode execution should be justified rather than assumed.

The guiding principle is:

**Prefer user-mode components; use kernel mode only when necessary.**

## 3.3 Kernel Independence

The kernel must remain independent of application programming languages, graphical environments, and specific filesystem implementations.

It should expose generic mechanisms through a stable and documented ABI.

The kernel must not contain OSLANG-specific concepts, application-specific behavior, or dependencies on particular user-mode frameworks.

# 4. User-Mode Services and Daemons

System services should be implemented as independent Ring3 processes whenever possible.

These processes are called **daemons**.

## 4.1 Naming Convention

System daemon executables should conventionally end with the letter `d`.

Examples:

- `fsd` — filesystem management daemon.
- `devd` — device management daemon.
- `netd` — network management daemon.
- `displayd` — graphical display service.

These names are illustrative. They do not imply that all listed components must exist or be implemented as separate processes.

The suffix is a naming convention, not an architectural requirement.

## 4.2 Daemon Responsibilities

Daemons should own higher-level system responsibilities that do not require direct kernel integration.

They should communicate through explicit, documented interfaces and existing OSB IPC capabilities.

A daemon should not require modifications to the kernel merely because it provides a system service.

## 4.3 Kernel-Level Exceptions

Some services or drivers may legitimately require kernel-mode implementation.

Such exceptions are allowed.

However, each exception should have a clear technical justification, a well-defined interface, and the smallest reasonable kernel footprint.

# 5. Drivers and Device Architecture

Drivers should be independent components whenever possible.

The system should favor a driver architecture that allows new devices and implementations to be introduced without modifying unrelated kernel subsystems.

Where technically feasible, drivers should execute in Ring3 and use documented kernel capabilities through the official SDK.

The architecture should support different privilege requirements without forcing every driver into kernel mode.

## 5.1 Future Language Integration

OSLANG is intended primarily as a high-level application programming language.

Writing device drivers directly in OSLANG is not a current architectural requirement.

However, future investigation may consider:

- An OSLANG-to-osbkt compatibility layer.
- Access to supported device interfaces through SDK APIs.
- High-level abstractions for simple device integrations.
- Restricted driver development scenarios where the underlying OSB interfaces permit them.

Such possibilities must not compromise the language's high-level nature or bypass kernel security boundaries.

Low-level driver development may continue to require other languages and toolchains.

# 6. Filesystem Philosophy

Filesystem implementations should be independent of the kernel.

OSB should not be permanently tied to a single filesystem.

The system should be able to support different filesystem implementations through a common OSB filesystem interface.

## 6.1 Filesystem Strategy

The initial filesystem choice is ext4.

Future possibilities include:

- Btrfs.
- FAT32.
- NTFS.
- Other useful filesystem implementations.

These are potential capabilities, not commitments to implement every filesystem.

A distinction should exist between:

1. Filesystem implementation.
2. Volume discovery and management.
3. Storage device access.
4. Public file and directory APIs.

The kernel should provide the necessary generic mechanisms, while filesystem-specific policies and structures belong outside it whenever practical.

## 6.2 No Mandatory POSIX Filesystem Model

OSB does not adopt the POSIX principle of treating everything as a file.

Files, directories, devices, processes, input events, and other system resources may have distinct APIs and semantics.

The system may reuse useful filesystem concepts from Unix-like operating systems without adopting their entire resource model.

# 7. System SDK and Public Interfaces

`osbkt` is the official OSB SDK.

It is the public boundary through which Ring3 applications and user-mode system components access kernel functionality.

Applications and runtimes must not bypass osbkt to access kernel interfaces directly.

The intended architecture is:

`Application → Runtime / Standard Library → osbkt → OSB Kernel`

This boundary should remain explicit, documented, and stable.

The SDK should expose useful OSB abstractions rather than merely exposing raw syscall details.

It should also avoid introducing abstractions that duplicate existing system capabilities without a clear benefit.

# 8. Shell and Command Philosophy

The OSB Shell is a fundamental part of the user experience.

The system should preserve the directness and discoverability associated with DOS-like command environments without attempting to reproduce DOS compatibility.

The Shell should provide a consistent way to:

- Execute applications.
- Manage files and directories.
- Configure the environment.
- Discover system capabilities.
- Manage available system components.
- Inspect and control system resources.

Commands should have clear and predictable behavior.

Where a graphical interface provides an operation, the equivalent operation should be available through the Shell whenever technically meaningful.

The Shell must remain useful independently of the graphical environment.

# 9. GUI Philosophy

The graphical environment should be an independent user-mode system service.

It must not be a mandatory component of the kernel.

The system should support configurations where it boots directly into a graphical environment or starts in a textual Shell.

The graphical environment should be designed around simplicity and consistency.

## 9.1 Desktop Experience

The intended experience draws inspiration from classic desktop environments, particularly the Windows 3.x Program Manager concept.

The system should favor:

- A program manager.
- Application groups.
- Clear application discovery.
- A straightforward file manager.
- Consistent window behavior.
- Simple system configuration.
- An approachable desktop environment.

The graphical environment should provide a coherent default experience instead of presenting users with multiple competing desktop environments.

## 9.2 Window Management and Composition

OSB should provide one coherent default graphical environment.

Window management and composition may be implemented as separate internal modules when that separation has a meaningful architectural benefit.

However, exposing multiple competing window managers or compositors is not a design objective.

Internal modularity should not translate into unnecessary user-facing choices.

The user should receive one consistent graphical experience.

# 10. GUI and Shell: One System, Two Interfaces

This is a foundational OSB principle.

**Every meaningful graphical operation should have an equivalent command-line operation whenever technically applicable.**

The GUI must not become an independent implementation of system administration and resource-management logic.

For example:

| Graphical operation | Shell equivalent |
|---|---|
| Create a directory | `MKDIR` |
| Delete a directory | `RMDIR` |
| Rename a file | `REN` |
| Copy a file | `COPY` |
| Launch an application | Application command |
| Inspect a system component | Corresponding system command |
| Manage a supported device | Corresponding device-management command |

## 10.1 Shared Command Infrastructure

The GUI and Shell should use the same underlying command infrastructure.

The GUI should invoke the same commands, operations, or shared command handlers that the Shell exposes.

The exact implementation may use:

- A shared command dispatcher.
- Reusable command handlers.
- Structured command requests.
- Existing SDK and daemon APIs.

The GUI does not necessarily need to launch a separate Shell process or parse textual command strings for every operation.

What matters is that the operation has one authoritative implementation and consistent observable behavior.

## 10.2 GUI as an Interface, Not a Separate System

The GUI should provide visual interaction and presentation.

It should not independently reimplement filesystem operations, device management, application discovery, or other system policies.

If an operation exists graphically, its command-line equivalent should exist unless there is a concrete reason why that operation is inherently graphical.

Conversely, Shell functionality should be accessible to graphical applications through supported shared interfaces where appropriate.

This principle ensures that:

- Automation remains possible.
- GUI and Shell behavior stay consistent.
- System operations are discoverable.
- The graphical environment remains replaceable.
- The textual environment remains first-class.

# 11. OSLANG and the Application Ecosystem

OSLANG is a first-class part of the OSB ecosystem.

It is designed as a high-level language with a straightforward syntax inspired by languages such as BASIC, Lua, and Python.

Its purpose is to make application development approachable without sacrificing the ability to build meaningful software.

OSLANG is not part of the kernel.

It runs through an independent Ring3 runtime and consumes OSB capabilities through osbkt and its standard library.

## 11.1 Language Philosophy

OSLANG should prioritize:

- Readable syntax.
- Low learning curve.
- Predictable semantics.
- A useful standard library.
- Natural integration with OSB.
- Practical application development.

The language should remain independent of the kernel's internal implementation.

## 11.2 Development Environment

Future development may include a visual development environment inspired by classic RAD tools.

Potential capabilities include:

- Visual form design.
- Drag-and-drop controls.
- Code editing.
- Declarative form descriptions.
- Designer-generated source artifacts.
- Integration with OSLANG.

The visual development environment should consume the same public OSB interfaces available to ordinary applications.

It must not require special kernel privileges or language-specific kernel functionality.

# 12. Avoiding Unnecessary Alternatives

OSB should not accumulate multiple implementations of the same capability without a clear reason.

When an existing subsystem already provides a capability:

1. Evaluate whether it can satisfy the new requirement.
2. Extend it when appropriate.
3. Reuse its public interfaces.
4. Introduce a new subsystem only when the existing one cannot reasonably fulfill the requirement.

This is not a prohibition against alternatives.

Different implementations may be justified by performance, compatibility, reliability, security, or specialized requirements.

However, the default OSB experience should remain coherent and predictable.

**Choice is valuable when it solves a real problem. Choice without a meaningful purpose is complexity.**

# 13. Architectural Evolution

The OSB architecture is expected to evolve.

Not every future capability must be designed or implemented today.

Architectural decisions should be made when supported by actual requirements, evidence, and technical constraints.

The project should favor:

- Small, independently ver
