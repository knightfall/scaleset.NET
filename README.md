# scaleset.NET

Planning a .NET 10 implementation of the GitHub Actions Runner Scale Set
client, listener, and a deployable autoscaler.

The migration preserves GitHub API and message protocol behavior while using
idiomatic asynchronous C# APIs. Phase one targets Docker. Phase two adds
separate ECS-task and direct EC2-instance providers.

## Planning documents

- [Migration plan](docs/migration/PLAN.md)
- [Decisions and open questions](docs/migration/DECISIONS.md)
- [Protocol parity checklist](docs/migration/PROTOCOL-PARITY.md)
- [Analysis and validation evidence](docs/migration/VALIDATION.md)
- [Upstream architecture](docs/codebase/ARCHITECTURE.md)
- [Stack](docs/codebase/STACK.md), [structure](docs/codebase/STRUCTURE.md),
  [conventions](docs/codebase/CONVENTIONS.md),
  [integrations](docs/codebase/INTEGRATIONS.md),
  [testing](docs/codebase/TESTING.md), and
  [concerns](docs/codebase/CONCERNS.md)

Planning status: **draft; user decisions are being collected**. There is no
C# implementation yet. The analysis baseline is
[`actions/scaleset` at e6daac7](https://github.com/actions/scaleset/tree/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5).
