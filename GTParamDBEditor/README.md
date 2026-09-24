# GT ParamDB Editor

A GUI editor for Gran Turismo 3 and [Gran Turismo Concept](#gt-concept) ParamDB files. It opens the
game's database as a live SQLite database you can edit in a grid, and writes the game files back out
every time you save.

Where `GTDataSQLiteConverter` is a two-step CLI (export to SQLite, edit it yourself, import back),
this keeps the SQLite database open behind the window, so there is no export/import cycle to run.
Both run on the same engine, so everything below holds for the CLI too.

## Using it

1. **File > Open** and pick `paramdb.db` (or `paramdb_us.db` / `paramdb_eu.db`, or a GT Concept one
   such as `paramdb_kr.db`). The status bar shows which game it was recognised as.
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
- **Add / Remove column** - Edit menu or right-click; see [Adding and removing columns](#adding-and-removing-columns).
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
you opened, plus a layout file under `Headers/Custom` if columns were added or removed (see
[below](#adding-and-removing-columns)). The first save copies each original to `<name>.bak` and
leaves that copy alone afterwards, so the `.bak` files are always the untouched originals. Turn this
off under **Tools > Back up game files on first save**.

Every file is built and checked in memory before anything on disk is touched. If a value does not
fit the format - 9999 in a byte column, text a Japanese column cannot encode - the save stops, lists
what is wrong, and leaves the game folder exactly as it was.

## Adding and removing columns

Rows can hold any number of columns, so a table can be given fields the retail game never had - the
GT Concept ones, say, or anything else a modified executable reads.

**Edit > Add column...** asks for a name, a type and an offset. The offset starts just after the
last column; type another to use bytes no column covers, like retail padding. If the column does not
fit in the row, the rows grow - always to a multiple of 8 bytes, like every table in both games. The
name starts as `Unk0x<offset>` until you type one.

A new column shows what its bytes already hold, so a column laid over existing bytes changes nothing
by itself. Bytes the rows gain start as zero, and text columns in them start empty.

**Edit > Remove column** removes the column under the cursor (or the one whose header you
right-clicked). If it was the last thing in the row, the rows shrink and what it held is gone.
Otherwise its bytes stay in every row as the file has them, so the columns after it keep their
offsets. The label column cannot be removed. Rows never shrink past bytes that hold data in the file.

Nothing on disk changes until you save. The grid and the SQL window see the new layout at once.

Saving writes the layout to `Headers/Custom/<GT3|GTC>/<TABLE>.headers`. That file is used for any
paramdb whose rows in that table are exactly the size it describes. So a file you widened opens with
its new columns, and an untouched retail file keeps the retail layout: opening it leaves a note that
the saved layout was not used. To widen another region's file the same way, add the same columns to
it. A saved layout with the same row size as the stock one (one that only names padding) applies to
every file of that game.

A layout saved after removing columns back to stock writes nothing and leaves the saved file in place,
for other files that still use it. Delete a file under `Headers/Custom` to stop using it. The layout
also travels in SQLite exports, so reopening one does not depend on the file.

The CLI's `add-column` and `remove-column` verbs do the same to a SQLite export, and its `import`
saves the layout the same way.

## Fidelity

Opening a ParamDB and saving it with no edits reproduces all five files byte for byte, for all three
regions of retail GT3 and for GT Concept's `paramdb_kr.db`. That is checked against the real game
files, along with:

- the label hash function, against all 20,714 entries of the ID table
- edits, insertions and deletions surviving a save and reload
- rows staying sorted by label hash, which the game's lookup depends on
- Japanese text surviving the euc-jp round trip
- out-of-range values being refused before anything is written
- all ordered table-to-table transitions in the window, driven through the real grid
- `carcolor.db` and `carcolor.sdb`, byte for byte, including through a full SQLite round trip
- edits to GT Concept's extra columns changing exactly the bytes they map, and a GT Concept database
  reopened from its SQLite export keeping GT Concept's layouts
- a column added to retail GEAR widening only that table, the widened file reopening with it, and
  removing it again giving back retail byte for byte

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

## GT Concept

GT Concept uses GT3's archive format and the same 36 tables, so the editor tells the two apart by
layout: whichever game's `.headers` files match the most blocks' row sizes wins. The differences:

- Tables 30-35 come in a different order - ENEMY_CARS, EVENT, REGULATIONS, COURSE, ARCADE_CAR, CAR -
  so CAR is table 35 rather than 30.
- Some rows hold fields retail GT3 does not have. What they mean is not known yet, so they are named
  after their offset:

  | Table        | Row size    | GT Concept-only fields                                   |
  |--------------|-------------|----------------------------------------------------------|
  | CHASSIS      | 0x20 → 0x28 | `Unk0x20`, `Unk0x22`, `Unk0x24` (ushort)                 |
  | ENGINE       | 0x58 → 0x60 | `Unk0x58` (byte)                                         |
  | GEAR         | 0x30 → 0x38 | `Unk0x30` (byte)                                         |
  | DRIVETRAIN   | 0x28        | `Unk0x22` (byte) - padding in retail                     |
  | RACINGMODIFY | 0x48        | `Unk0x41` (byte), `Unk0x42` (ushort) - padding in retail |

- The end of an EVENT row follows GT Concept's own getters, which do not match the retail layout:
  `TwGoodTireWear` and `TwGoodTireGripDown` are separate bytes, `LaunchPoint` is a ushort,
  `NeedDrivetrain` is a byte, and `LightEffect` is the byte after it, at `+0x1F7`.

These layouts live in `Headers/GTC/`. A table with no file there uses the shared one in `Headers/`,
and includes resolve the same way - so renaming an `Unk` column means editing one small file.

Not covered: some GT Concept files are stored gzip-compressed (they start with `1F 8B`), including
the `carcolor.db` that sits beside `paramdb_kr.db`. Those are not read; decompress them first.
`paramdb_kr.db` and its companion files are not compressed.

## Building

```
dotnet build GTParamDBEditor -c Release
```

The engine - reading and writing the game files, and their SQLite form - is the parent project's
`ParamDb/`, shared with the CLI. Only the window, and the session it keeps open, live here.

Table layouts come from the `.headers` files in the parent project (GT Concept's own in
`Headers/GTC/`), which are copied next to the executable. Editing those changes what the editor shows
without a rebuild.
