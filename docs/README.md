# Documentation

This directory is the index for documentation that applies across the socAlytics monorepo.

## Documentation Areas

- [Architecture](architecture/README.md) contains the authoritative current system design, shared diagrams, and durable architecture decisions.
- [`openspec/specs/`](../openspec/specs/) contains accepted behavioral requirements and scenarios.
- [`openspec/changes/`](../openspec/changes/) contains active change state and archived change history.

## Conventions

Add documentation with the implementation it describes:

- Keep component-specific setup, design, and usage guidance close to the relevant component and link it from here when it has repository-wide value.
- Keep executable interfaces such as OpenAPI and JSON Schema with the component that owns and validates them.
- Add operational guidance when deployable workloads and supported environments exist.

Return to the [repository overview](../README.md).
