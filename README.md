# GTDataSQLiteConverter

Currently GT3 & export to SQLite only.

Two programs share the table layouts in `Headers/`:

- **GTDataSQLiteConverter** - the CLI. `export` a paramdb to a SQLite database, edit it however you
  like, `import` it back.
- **[GTParamDBEditor](GTParamDBEditor/README.md)** - a GUI that opens a paramdb directly as a live
  SQLite database and writes the game files back every time you save.

Major credits to [pez2k's GT3DataSplitter](https://github.com/pez2k/gt2tools/tree/master/GT3DataSplitter) for providing a base for this project, and to [Nenkai](https://github.com/Nenkai/) for various work, organisation and redesigns on a private version of this tool used for a different game.
