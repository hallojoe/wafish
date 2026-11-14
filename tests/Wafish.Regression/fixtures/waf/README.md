# WAF behavior corpus

These synthetic cases were written for this repository on 2026-11-14.
They contain no captured customer requests or credentials.

`kind` distinguishes benign API data from attack fixtures.
`knownRejection` identifies documented false positives from deliberately broad rules.
The runner reports rejected benign names and missed attack names separately.
A new benign rejection or missed attack fails the check.
Known rejections are explicit behavior limits, not evidence of complete attack detection.

Keep literal Unicode, malformed percent escapes, and mixed path encoding in this fixture.
Run `--protocol-properties` to replay the corpus and generated seeds.
