# Isolated package consumers

This source template is compiled outside repository build imports by `scripts/verify-packages.py`.
Profiles cover runtime, administration, and React UI hosting with plugin composition.
A fresh NuGet cache and source mapping require locally produced WafIsh packages.
Checks verify native SQLite3MC loading, encrypted reopening, wrong-key rejection, authentication, and embedded assets.
CI runs these checks on Linux, Windows, and macOS.
