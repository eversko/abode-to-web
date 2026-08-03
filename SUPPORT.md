# Support

Abode to Web is a free, solo-maintained open-source preview. Support is best effort and there is no guaranteed response or resolution time.

## Start here

- Installation and upgrades: [docs/INSTALLATION.md](docs/INSTALLATION.md)
- Adding an application: [docs/ADDING_A_PROJECT.md](docs/ADDING_A_PROJECT.md)
- Daily operation: [docs/OPERATIONS.md](docs/OPERATIONS.md)
- Common failures: [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- Security boundaries: [docs/SECURITY.md](docs/SECURITY.md)

The execution-free static path is the supported 0.1 preview and has been validated end to end on one Windows host; it is not yet a generally supported stable release. Node, direct-command, and Docker Compose workloads are experimental.

## Where to ask

| Need | Channel |
| --- | --- |
| Reproducible Abode to Web bug | Public GitHub issue, after removing private data |
| Usage or configuration question | Public **Question** issue form, after removing private data |
| Small feature proposal | Public GitHub issue |
| Vulnerability | [Private vulnerability report](https://github.com/eversko/abode-to-web/security/advisories/new) |
| Conduct concern | Public **Conduct contact** issue form; it requests a private channel and must contain no names or evidence |

Do not post tokens, secret values, private repository URLs, personal paths, tunnel credentials, full manifests, or unredacted logs. A maintainer will not ask you to post a secret publicly.

## A useful bug report

Include:

- Abode to Web commit or preview version;
- Windows edition and version;
- deployment type: static, Node, direct command, or Docker Compose;
- the exact action attempted;
- expected and observed behavior;
- the smallest safe reproduction; and
- sanitized error text and relevant operation ID.

If an LLM or coding agent generated the setup, include the final reviewed plan or manifest excerpt, not the full conversation. You remain responsible for approving commands and removing private information.

## Support boundary

The project can help diagnose Abode to Web behavior. It cannot provide general Windows administration, recover third-party accounts, write Cloudflare policy, debug every hosted application, or guarantee that a particular dependency is safe. Unsupported network exposure, modified installers, and older preview versions may be closed with an upgrade or safe-configuration recommendation.

For a security-sensitive question, err on the side of the [private reporting process](SECURITY.md).
