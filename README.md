# socAlytics

socAlytics is a planned polyglot monorepo. The repository currently contains its governance and documentation foundation; technology-specific components will be added when their requirements are defined.

## Repository Structure

- [`docs/`](docs/README.md) indexes architecture, component, and operational documentation.
- [`openspec/`](openspec/) contains accepted behavioral specifications, active changes, and archived change history.
- [`AGENTS.md`](AGENTS.md) defines repository guidance for coding agents.
- [`LICENSE`](LICENSE) contains the project license.
- `.editorconfig` provides stack-neutral text-file defaults.

Code, test, infrastructure, and automation directories will be introduced with their first meaningful artifacts rather than as empty placeholders.

## Development

No application stack, dependency manager, build, lint, or test command has been
selected yet. Add and document those commands here when the first component is
introduced.

### OpenSpec

The repository uses OpenSpec `1.13.0` as its user-facing change workflow. The
CLI is a global development tool and does not require an application
`package.json`:

```powershell
npm install --global @fission-ai/openspec@1.13.0
openspec --version
```

After cloning, run `openspec update` to refresh the generated GitHub Copilot
prompts and skills for the configured workflow profile. Restart VS Code after an
update so prompt discovery refreshes.

Start work through the generated GitHub Copilot prompts:

- `/opsx-explore` investigates an idea without creating change artifacts.
- `/opsx-propose` creates a proposal, behavioral specification delta, design,
  and owned task list.
- `/opsx-apply` implements approved tasks through their declared `soca-*` owner.
- `/opsx-update` continues or refreshes change artifacts.
- `/opsx-sync` synchronizes accepted behavioral requirements.
- `/opsx-verify` independently checks the completed change and evidence.
- `/opsx-archive` archives a verified change and updates accepted specs.

Useful repository checks are:

```powershell
openspec doctor --json
openspec schema validate spec-driven --json
openspec validate --all --json
openspec status --json
```

Architecture narratives remain authoritative for current system design;
`openspec/specs/` is authoritative for accepted behavioral requirements.

## License

This project is licensed under the [MIT License](LICENSE).
