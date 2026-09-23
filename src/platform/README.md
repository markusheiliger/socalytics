# Platform

## Purpose

This area is reserved for the SocAlytics control-plane platform.

## Planned Ownership

The planned boundary includes the API/BFF and the ASP.NET Core modular monolith that owns platform application behavior and coordinates platform services.

## Exclusions

Client application source, agent runtimes and tooling, Analyst execution software, and Analyst capabilities do not belong here. This scaffold contains no projects, packages, tests, manifests, deployment configuration, or product code.

## Current Status

This directory is non-executable. It currently records only a planned ownership boundary and does not provide build, test, runtime, or deployment support.

## Authoritative Architecture

- [Architecture overview](../../docs/architecture/overview.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
- [Contracts and compatibility](../../docs/architecture/contracts-and-compatibility.md)
- [Job processing](../../docs/architecture/job-processing.md)
