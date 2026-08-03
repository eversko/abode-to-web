# Changelog

Notable user-visible changes are recorded here. Abode to Web is pre-release; behavior and configuration may change between `0.x` previews.

This project follows the structure of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and intends to use [Semantic Versioning](https://semver.org/) when public versions are tagged.

## Unreleased

### Added

- Initial Apache-2.0 open-source preparation under the Abode to Web name.
- Windows Service control plane, loopback dashboard, and separate public application gateway.
- Exact-commit candidate deployments with health-gated route activation and retained-release rollback.
- Static application hosting, local audit history, bounded logs, backup tooling, and guided Cloudflare Tunnel configuration.
- Community, support, governance, security, threat-model, and release-readiness documentation.

### Security

- Defined the public/private reporting boundary and product-specific security invariants.
- Converted the repository-wide security and readiness review into explicit release gates.

### Known limitations

- The execution-free static path is the supported 0.1 preview and has been validated end to end on one Windows host; it is not yet a generally supported stable release.
- Node, direct-command, and Docker Compose deployments are experimental until stronger isolation and artifact-integrity gates are complete.
- Release checksums are not code signatures.
- There is no stable release line or compatibility guarantee yet.
