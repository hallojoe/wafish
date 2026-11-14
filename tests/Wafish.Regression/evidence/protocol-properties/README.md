# Generated protocol checks

Completed: 2026-11-14. Base commit: `bc1b617`. Changes remain uncommitted.

## Results

- Release solution build: zero warnings and errors.
- Main regression suite: 214 passing cases. Administration suite: 35 passing cases.
- All three isolated package consumers and public API checks pass.
- Changed production C# executable lines: 16/16, or 100%.
- WAF corpus: 16 benign cases and 14 attack cases, each tested with three read sizes.
- Rejected benign cases decreased from eight to two. Missed attack cases decreased from one to zero.
- The two remaining benign rejections are explicit corpus limits: standalone SQL verbs and HTML tutorial content.
- WAF generation: 1,226 executions across seeds 17, 91, and 2026, Unicode, malformed escapes, byte limits, and mixed traversal encodings.
- Cache generation: 694 freshness, age, directive, and variant cases with an injected monotonic clock.
- Compression generation: 129 quality, duplicate, wildcard, malformed-field, and multiline cases. Decompressed bytes match the input.
- Metrics: 6,144 concurrent updates across three seeds. Every observed snapshot preserves coherent counters.

## Corrections

SQL keywords now require a trailing word boundary. Procedure prefixes remain supported.
XSS handler detection starts at a word boundary, avoiding embedded fragments in ordinary form keys.
Traversal checks distinguish encoded separators from individual percent, five, and c characters.
Mixed raw and encoded traversal is rejected. Paired double-encoded dot markers retain their prior conservative rejection.
Compression validates complete lists and the original quality syntax before choosing a coding.
Quality values follow [RFC 9110 section 12.4.2](https://httpwg.org/specs/rfc9110.html#quality.values).
Malformed fields preserve the uncompressed upstream response.
No public API changes were required.

## Commands

```bash
dotnet build Wafish.slnx -c Release
WAFISH_TEST_REPORT=/tmp/wafish-main.json dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish-main.xml 'dotnet run --project tests/Wafish.Regression -c Release --no-build'
WAFISH_TEST_REPORT=/tmp/wafish-admin.json dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish-admin.xml 'dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build'
dotnet tool run dotnet-coverage merge /tmp/wafish-main.xml /tmp/wafish-admin.xml -f xml -o /tmp/wafish-coverage.xml
python tests/Wafish.Regression/evidence/protocol-properties/changed-coverage.py /tmp/wafish-coverage.xml
python scripts/verify-packages.py
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --protocol-properties --seed 91
```

The seed selects generated WAF and metric inputs. Fixed corpus and header matrices always run.
Metric interleavings can vary; barriers establish the ownership boundaries and assertions verify every observed snapshot.
The corpus is synthetic and does not establish complete attack detection.
The initial failing log also contains two harness-model errors, corrected before final verification:
an empty Cache-Control assignment created a present malformed header, and generated hyphens could form an intentional SQL-comment rejection.
