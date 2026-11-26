import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { extractArchiveSection } from "../src/lib/archive.ts";

const record = (name, body) => `<!-- archive:start ${name} -->\n${body}\n<!-- archive:end ${name} -->`;

test("archive extraction isolates one record and restores heading levels", () => {
  const markdown = record("before", "PRIVATE BEFORE") + "\n" + record("release", "### Release\n#### Features\n```md\n### unchanged code\n```") + "\n" + record("after", "PRIVATE AFTER");
  assert.equal(extractArchiveSection(markdown, "release"), "# Release\n## Features\n```md\n### unchanged code\n```");
});

test("missing, duplicate, reversed, and overlapping boundaries fail closed", () => {
  for (const markdown of [
    "missing",
    record("release", "one") + "\n" + record("release", "two"),
    "<!-- archive:end release -->\n<!-- archive:start release -->",
    record("release", record("other", "overlap")),
    "<!-- archive:start release -->\nmissing end",
  ]) assert.throws(() => extractArchiveSection(markdown, "release"));
});

test("real release source excludes adjacent historical documents", async () => {
  const archive = await readFile(new URL("../../ARCHIVE.md", import.meta.url), "utf8");
  const release = extractArchiveSection(archive, "archive-docs-release-notes-1-0-0-rc-1-md");
  assert.match(release, /^# .*1\.0\.0-rc\.1/m);
  assert.match(release, /^## Verification/m);
  assert.match(release, /Prepared: 2026-11-15/);
  assert.doesNotMatch(release, /## Source inventory|# Wafish remaining work|# Public package release scope/);
});
