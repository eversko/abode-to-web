# Governance

Abode to Web uses a maintainer-led governance model. The project is currently maintained by one person, [@Jstn-1g](https://github.com/Jstn-1g), with community input through issues and pull requests.

## Roles

- **Users** report problems, ask questions, and share operational evidence.
- **Contributors** propose and implement focused improvements under the project license and contribution rules.
- **Maintainers** set scope, merge changes, manage releases, handle security reports, and protect the project's safety boundaries.

Roles are earned through sustained, constructive participation and can be expanded as the contributor base grows. There is currently no steering committee, paid support tier, or guaranteed path to maintainership.

## Decision making

Routine decisions happen in issues and pull requests. The maintainer seeks useful evidence and contributor input, then makes the final call. Decisions prioritize:

1. safety of the local control plane and hosted applications;
2. predictable failure and recovery;
3. a focused one-person, one-Windows-machine use case;
4. maintainability for a small contributor base; and
5. user value supported by real operational evidence.

Large changes should begin with an issue describing the problem, proposed boundary, alternatives, security impact, migration, and rollback. Silence is not approval, and a roadmap item is not a promise to merge a particular implementation.

## Releases and security

The maintainer decides when a commit is ready for a preview or stable release. Release criteria are documented in [docs/ROADMAP.md](docs/ROADMAP.md). Security reports remain private until a coordinated disclosure is safe; they follow [SECURITY.md](SECURITY.md).

No one may bypass required review, tests, or release gates solely to meet a date. The Apache-2.0 license allows anyone to fork the project when priorities differ, provided notices and license obligations are preserved.

## Project continuity

Solo maintenance is a bus-factor risk. Abode to Web reduces that risk through public source, reproducible checks, documented operations, reviewable decisions, and permissive licensing. If maintenance changes hands, the repository should record the new maintainers and decision process before presenting the transition as official.

This governance document may evolve as trusted maintainers and regular contributors emerge.
