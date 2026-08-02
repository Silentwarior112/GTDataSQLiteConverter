# GT ParamDB Editor

A GUI editor for Gran Turismo 3 ParamDB files. It opens the game's database as a live SQLite
database you can edit in a grid, and writes the game files back out every time you save.

Where `GTDataSQLiteConverter` is a two-step CLI (export to SQLite, edit it yourself, import back),
this keeps the SQLite database open behind the window, so there is no export/import cycle to run.

## Using it

1. **File > Open** and pick `paramdb.db` (or `paramdb_us.db` / `paramdb_eu.db`).
   The companion files - `paramstr`, `paramunistr`, `.id_db_idx`, `.id_db_str` - are found next to it
   by name. `paramstr` and `paramunistr` must be present; without the `.id_db_*` pair, row labels
   show as raw hashes instead of names.
2. Pick a table on the left, edit cells on the right. Hovering a column header shows its type, its
   offset in the row, and any notes from the `.headers` file (e.g. what each bit of `CAR.Flags` does).
3. **File > Save** (Ctrl+S) writes all five game files back over the originals.

If `carcolor.db` and `carcolor.sdb` sit in the same folder they are picked up too, and appear as two
more tables:

- **CARCOLOR_PALETTE** - the shared palette: `ColorId`, `Rgb` (as `#RRGGBB`), and the western and
  Japanese names. Editing a colour changes it for every car that offers it.
- **CARCOLOR_CARS** - one row per colour slot: which car, which position in its list, which colour.
  `Slot` is the order the showroom offers them in, so it is deliberately not sorted.

Both save back into `carcolor.db` / `carcolor.sdb`, which have no region suffix - one pair is shared
by all three regions. A `ColorId` that matches no palette entry stops the save rather than shipping a
file the game would look up and miss.

Also available:

- **Add / Duplicate / Delete row** - Ctrl+N, Ctrl+D, Ctrl+Delete, or the right-click menu.
  New rows get a unique placeholder label, which you should rename to something meaningful.
- **Filter rows** - the box above the grid does a substring match across every column.
- **Tools > SQL query** (Ctrl+Q) - run SQL against the live database. Good for bulk edits:
  `UPDATE CAR SET Price = Price / 2 WHERE Year < 1990;`
- **File > Export SQLite copy** - a snapshot you can hand to the CLI converter's `import` verb,
  or just keep around.
- **File > Open** also accepts a `.sqlite` file, including one exported here or by the CLI converter.
  Editor exports remember which folder they came from, so Save still goes to the right place.
- **File > Reload from disk** - throw away everything since the last save.

## What it does to your files

Saving replaces `paramdb`, `paramstr`, `paramunistr`, `.id_db_idx` and `.id_db_str` for the region
you opened. The first save copies each original to `<name>.bak` and leaves that copy alone
afterwards, so the `.bak` files are always the untouched originals. Turn this off under
**Tools > Back up game files on first save**.

Every file is built and checked in memory before anything on disk is touched. If a value does not
fit the format - 9999 in a byte column, text a Japanese column cannot encode - the save stops, lists
what is wrong, and leaves the game folder exactly as it was.

## Fidelity

Opening a ParamDB and saving it with no edits reproduces all five files byte for byte, for all three
regions of retail GT3. That is checked against the real game files, along with:

- the label hash function, against all 20,714 entries of the ID table
- edits, insertions and deletions surviving a save and reload
- rows staying sorted by label hash, which the game's lookup depends on
- Japanese text surviving the euc-jp round trip
- out-of-range values being refused before anything is written
- all ordered table-to-table transitions in the window, driven through the real grid
- `carcolor.db` and `carcolor.sdb`, byte for byte, including through a full SQLite round trip

A few details this relies on, all confirmed against retail files:

- blocks in the archive start on 8-byte boundaries, and the trailing index entry is relative to the
  index size like every other entry
- `paramstr` and `.id_db_str` store the bare string length, `paramunistr` stores the padded length
  including the terminator
- `paramunistr` contains a handful of byte sequences that are not valid euc-jp. Decoding them is
  lossy, so strings that came from the file are written back as the exact bytes that were read;
  only strings you actually change get re-encoded.
- string and ID tables are extended rather than rebuilt, so indices held by data the `.headers`
  files do not describe stay pointing at the right strings

Rows are also written starting from their original bytes, matched by label, so anything a `.headers`
file does not cover survives an edit to the same row. Tables with no mapping at all are passed
through untouched.

### carcolor.db

The "GT2K" file is a three-level structure - car, its ordered list of colour ids, and the shared
palette those ids resolve against:

```
+0x00  u32  magic "GT2K"
+0x04  u32  zero
+0x08  u32  car count                        <- a count, not an offset
+0x0C  u32  colour-id pool offset
+0x10  u32  palette offset
+0x14  u32  file size
+0x18       car count x { u64 labelHash, u32 colourCount, u32 poolOffset }, sorted by hash
pool        [u32 count][count x u32 colour id]
palette     [u32 count][count x { u32 id, u32 latinName, u32 japaneseName, u32 bgr }], sorted by id
```

Four things are easy to get wrong here, and the editor hides all of them:

- `poolOffset` is measured from the pool's own start, so offset 4 is the first id and the count word
  is never pointed at. The runs tile the pool exactly - no gaps, no sharing.
- The pool holds colour **ids**, not indices. Ids are sparse (612 of 1..1511), so lookup is a search.
- The swatch is **BGR** - `0x00BBGGRR`, bytes in file order R,G,B,00. Read as RGB every red car turns
  blue. The editor shows and accepts `#RRGGBB` and converts on save.
- The two name fields are separate indices that happen to be equal in 383 of 612 retail entries, so a
  small sample makes them look like one duplicated field.

Retail `carcolor.db` does not store the runs in car order and there is no rule to re-derive, so the
offsets a file was read with are kept while they still tile the pool. Once colour lists are edited
they no longer do, and the pool is laid out afresh - still valid, but no longer byte-identical to
retail. That is expected.

## Building

```
dotnet build GTParamDBEditor -c Release
```