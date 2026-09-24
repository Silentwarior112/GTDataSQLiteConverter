# GTDataSQLiteConverter

Converts Gran Turismo 3 and Gran Turismo Concept paramdbs to SQLite databases and back.

Two programs share one engine (`ParamDb/`) and the table layouts in `Headers/`:

- **GTDataSQLiteConverter** - the CLI. `export` a paramdb to a SQLite database, edit it however you
  like, `import` it back.
- **[GTParamDBEditor](GTParamDBEditor/README.md)** - a GUI that opens a paramdb directly as a live
  SQLite database and writes the game files back every time you save.

Because the engine is shared, a paramdb and its SQLite export behave the same in both. Either tool
opens the other's exports, GT Concept files are recognised (their layouts are in `Headers/GTC/`),
columns added in one are known to the other, and a round trip with no edits reproduces the game
files byte for byte. The editor's README has the details.

## CLI

```
GTDataSQLiteConverter export -i paramdb_us.db [-o paramdb_us.sqlite]
GTDataSQLiteConverter import -i paramdb_us.sqlite [-o <folder>] [-s <suffix>] [--backup]
GTDataSQLiteConverter add-column -i paramdb_us.sqlite -t GEAR -n GearType --type byte [--offset 30]
GTDataSQLiteConverter remove-column -i paramdb_us.sqlite -t GEAR -n GearType
```

- `export` finds `paramstr`, `paramunistr` and the `.id_db_*` pair next to the paramdb by its suffix
  (`paramdb_us.db` -> `paramstr_us.db`, ...). `--pstr`, `--ustr`, `--idx`, `--istr` and `--cstr` point
  at them explicitly. A `carcolor.db` / `carcolor.sdb` pair next to it is exported too.
- `import` writes the game files into the SQLite file's folder, or `-o`. The suffix defaults to the
  one the paramdb was exported with. `--backup` copies each file it replaces to `<name>.bak` first,
  the first time only. If a value does not fit the format nothing is written and the problems are
  listed. The SQLite file itself is left unchanged.
- `add-column` and `remove-column` change a table's columns in a SQLite export, like the editor's Edit
  menu. The next `import` widens or shrinks the rows and saves the layout under `Headers/Custom`.
  Types are spelled as in `.headers` files; the offset, in hex, defaults to just after the last column.

Every verb exits with 1 when it fails.

Exports from older versions of the CLI still import. They never kept the bytes no `.headers` file
describes, though, so those cannot come back.

Major credits to [pez2k's GT3DataSplitter](https://github.com/pez2k/gt2tools/tree/master/GT3DataSplitter) for providing a base for this project, and to [Nenkai](https://github.com/Nenkai/) for various work, organisation and redesigns on a private version of this tool used for a different game.
