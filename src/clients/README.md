# Clients

## Purpose

This area is reserved for SocAlytics user-facing client applications.

## Planned Ownership

The planned boundary includes the React Web UI, the Electron Coach Client, and environment-neutral TypeScript sharing between those application shells. Browser-specific behavior, Electron IPC, offline storage, and workflow-specific UI remain owned by their eventual applications.

## Exclusions

Platform API implementation, server runtime composition, agent runtimes, and Analyst execution software do not belong here. This scaffold contains no applications, packages, tests, manifests, deployment configuration, or product code.

## Current Status

This directory is non-executable. It currently records only a planned ownership boundary and does not provide build, test, package, runtime, or deployment support.

## Authoritative Architecture

- [Client applications](../../docs/architecture/client-applications.md)
- [Client decision evidence](../../docs/architecture/client-decision-evidence.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
