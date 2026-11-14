# WafIsh.Storage.Sqlite

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This project implements the storage contracts with SQLite and optional SQLCipher-compatible encryption through SQLite3MC.

```mermaid
flowchart LR
    P0["Repository call"]
    P1["Connection factory and migrations"]
    P2["SQLite transaction"]
    P3["Local database and WAL"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

A branch-office gateway stores routes and audit records locally. It restores the saved configuration after a restart.

## Real-world value

A gateway can retain its state without operating a separate database server.

## What it does

- Initialize connections and apply ordered schema migrations.
- Persist configuration snapshots with version checks and transactions.
- Store plugin state, logs, audit history, notifications, and AI records.

## What it does not do

- Provide a shared multi-node configuration consensus system.
- Replace the host responsibility for protecting encryption keys and backup files.

## Start here

Source project: [WafIsh.Storage.Sqlite.csproj](WafIsh.Storage.Sqlite.csproj).
Direct project references: [WafIsh.Storage.Abstractions](../WafIsh.Storage.Abstractions/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Review fixes 4–7

Migration 019 adds a binding revision table and transactional change triggers.
Atomic batches reject stale revisions and roll back all changes when any operation fails.
Direct binding writes and configuration identity updates also advance this revision.

## Proxy log time comparisons

Proxy log ranges, ordering, aggregation, and retention compare UTC instants with `julianday(Timestamp)`.
Recent error counts exclude expired and future records.
Migration 020 adds the matching expression index to existing databases.
Existing timestamp text and offsets remain unchanged. New proxy log timestamps use UTC.
SQLite interprets offset-free legacy timestamps as UTC. Comparisons have millisecond precision.
Malformed timestamps produce no comparable instant and remain outside time-range queries.

[Verification for review items 8–10](../../tests/WafIsh.Regression/evidence/review8910/README.md).

## Review items 14–16

Stored timestamps use invariant UTC round-trip text with seven fractional digits and a `Z` suffix.
Migration 021 normalizes valid legacy timestamps while preserving their instants and precision.
Offsetless legacy text is interpreted as UTC. Invalid text and null values remain unchanged.
Migration pages contain at most 256 records and support composite keys and WITHOUT ROWID tables.
Readers normalize parsed timestamps to UTC. Proxy-log range comparisons retain indexed `julianday` semantics.
Register `TimeProvider` before storage registration to control recent-log windows and retention cutoffs.

Verification: [review evidence](../../tests/WafIsh.Regression/evidence/review141516/README.md).
