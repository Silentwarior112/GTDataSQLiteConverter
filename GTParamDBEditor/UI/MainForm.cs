using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Media;
using System.Text;

using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.ParamDb;

using GTParamDBEditor.Core;

namespace GTParamDBEditor.UI;

public sealed class MainForm : Form
{
    private const string AppName = "GT ParamDB Editor";

    private EditorSession? _session;
    private LiveTable? _currentTable;
    private DataTable? _currentData;
    private SqlQueryForm? _sqlForm;
    private bool _suppressTableChange;

    /// <summary>The column last right-clicked, so the context menu can act on a header's column.</summary>
    private LiveColumn? _rightClickedColumn;

    private readonly BindingSource _binding = new();

    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new();
    private readonly StatusStrip _statusBar = new();
    private readonly SplitContainer _split = new();
    private readonly ListView _tableList = new();
    private readonly TextBox _tableFilter = new();
    private readonly TextBox _rowFilter = new();
    private readonly DataGridView _grid = new();
    private readonly Label _gridHint = new();

    private readonly ToolStripStatusLabel _statusFile = new();
    private readonly ToolStripStatusLabel _statusSpring = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusRows = new();
    private readonly ToolStripStatusLabel _statusDirty = new();

    private readonly ToolStripMenuItem _saveItem = new("&Save to game files") { ShortcutKeys = Keys.Control | Keys.S };
    private readonly ToolStripMenuItem _saveAsItem = new("Save &as...");
    private readonly ToolStripMenuItem _exportItem = new("&Export SQLite copy...");
    private readonly ToolStripMenuItem _revertItem = new("&Reload from disk");
    private readonly ToolStripMenuItem _closeItem = new("&Close");
    private readonly ToolStripMenuItem _addRowItem = new("&Add row") { ShortcutKeys = Keys.Control | Keys.N };
    private readonly ToolStripMenuItem _duplicateRowItem = new("&Duplicate row") { ShortcutKeys = Keys.Control | Keys.D };
    private readonly ToolStripMenuItem _deleteRowItem = new("De&lete rows") { ShortcutKeys = Keys.Control | Keys.Delete };
    private readonly ToolStripMenuItem _addColumnItem = new("Add &column...");
    private readonly ToolStripMenuItem _removeColumnItem = new("Re&move column");
    private readonly ToolStripMenuItem _contextAddColumn = new("Add column...");
    private readonly ToolStripMenuItem _contextRemoveColumn = new("Remove column");
    private readonly ToolStripMenuItem _sqlItem = new("SQL &query...") { ShortcutKeys = Keys.Control | Keys.Q };
    private readonly ToolStripMenuItem _warningsItem = new("Show load &warnings...");
    private readonly ToolStripMenuItem _backupItem = new("Back up game files on first save") { CheckOnClick = true, Checked = true };

    private readonly ToolStripButton _toolbarSave = new("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton _toolbarAdd = new("Add row") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton _toolbarDelete = new("Delete rows") { DisplayStyle = ToolStripItemDisplayStyle.Text };

    private readonly string? _initialPath;

    public MainForm(string? initialPath)
    {
        _initialPath = initialPath;

        InitializeComponent();
        UpdateChrome();
    }

    // ---------------------------------------------------------------- layout

    private void InitializeComponent()
    {
        Text = AppName;
        MinimumSize = new Size(900, 520);
        Size = new Size(1280, 780);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildStatusBar();

        // Tables list
        var listPanel = new Panel { Dock = DockStyle.Fill };

        _tableList.View = View.Details;
        _tableList.Dock = DockStyle.Fill;
        _tableList.FullRowSelect = true;
        _tableList.MultiSelect = false;
        _tableList.HideSelection = false;
        _tableList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _tableList.Columns.Add("Table", 190);
        _tableList.Columns.Add("Rows", 60, HorizontalAlignment.Right);
        _tableList.SelectedIndexChanged += OnTableSelected;

        _tableFilter.Dock = DockStyle.Top;
        _tableFilter.PlaceholderText = "Filter tables...";
        _tableFilter.Margin = new Padding(0);
        _tableFilter.TextChanged += (_, _) => PopulateTableList();

        listPanel.Controls.Add(_tableList);
        listPanel.Controls.Add(_tableFilter);

        // Grid side
        var gridPanel = new Panel { Dock = DockStyle.Fill };

        _grid.Dock = DockStyle.Fill;

        // Columns are built by hand in BuildGridColumns. Letting DataGridView generate them from the
        // bound DataTable means it also decides what to keep across a rebind, and it will happily
        // re-add a frozen column behind an unfrozen one and then throw on it.
        _grid.AutoGenerateColumns = false;

        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = true;
        _grid.AllowUserToOrderColumns = true;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowHeadersWidth = 46;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.ShowCellToolTips = true;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 247, 250);
        _grid.DataSource = _binding;
        _grid.DataError += OnGridDataError;
        _grid.CellFormatting += OnGridCellFormatting;
        _grid.DataBindingComplete += (_, _) => UpdateRowStatus();
        _grid.CurrentCellChanged += (_, _) =>
        {
            UpdateRowStatus();
            UpdateColumnCommands();
        };
        _grid.UserDeletedRow += (_, _) => MarkDirty();
        _grid.CellValueChanged += (_, _) => MarkDirty();

        _gridHint.Dock = DockStyle.Fill;
        _gridHint.TextAlign = ContentAlignment.MiddleCenter;
        _gridHint.Text = "File > Open a paramdb.db to start editing.";
        _gridHint.ForeColor = SystemColors.GrayText;

        var filterBar = new Panel { Dock = DockStyle.Top, Height = 30 };
        _rowFilter.Dock = DockStyle.Fill;
        _rowFilter.PlaceholderText = "Filter rows (searches every column)...";
        _rowFilter.TextChanged += (_, _) => ApplyRowFilter();
        var clearFilter = new Button { Text = "Clear", Dock = DockStyle.Right, Width = 70, FlatStyle = FlatStyle.System };
        clearFilter.Click += (_, _) => _rowFilter.Clear();
        filterBar.Controls.Add(_rowFilter);
        filterBar.Controls.Add(clearFilter);
        filterBar.Padding = new Padding(0, 3, 0, 3);

        gridPanel.Controls.Add(_grid);
        gridPanel.Controls.Add(_gridHint);
        gridPanel.Controls.Add(filterBar);

        _grid.Visible = false;

        _split.Dock = DockStyle.Fill;
        _split.FixedPanel = FixedPanel.Panel1;
        _split.Panel1.Controls.Add(listPanel);
        _split.Panel2.Controls.Add(gridPanel);

        Controls.Add(_split);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        Controls.Add(_statusBar);
        MainMenuStrip = _menu;

        BuildGridContextMenu();
    }

    private void BuildMenu()
    {
        var open = new ToolStripMenuItem("&Open...", null, (_, _) => OpenWithDialog()) { ShortcutKeys = Keys.Control | Keys.O };

        _saveItem.Click += (_, _) => Save();
        _saveAsItem.Click += (_, _) => SaveAs();
        _exportItem.Click += (_, _) => ExportSqlite();
        _revertItem.Click += (_, _) => Revert();
        _closeItem.Click += (_, _) => CloseSession(prompt: true);

        var exit = new ToolStripMenuItem("E&xit", null, (_, _) => Close());

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            open, new ToolStripSeparator(),
            _saveItem, _saveAsItem, new ToolStripSeparator(),
            _exportItem, _revertItem, _closeItem, new ToolStripSeparator(),
            exit,
        });

        _addRowItem.Click += (_, _) => AddRow();
        _duplicateRowItem.Click += (_, _) => DuplicateRow();
        _deleteRowItem.Click += (_, _) => DeleteSelectedRows();
        _addColumnItem.Click += (_, _) => AddColumn();
        _removeColumnItem.Click += (_, _) => RemoveColumn(CurrentColumn());

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            _addRowItem, _duplicateRowItem, _deleteRowItem, new ToolStripSeparator(),
            _addColumnItem, _removeColumnItem,
        });

        _sqlItem.Click += (_, _) => ShowSqlQuery();
        _warningsItem.Click += (_, _) => ShowWarnings();

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.AddRange(new ToolStripItem[] { _sqlItem, _warningsItem, new ToolStripSeparator(), _backupItem });

        var about = new ToolStripMenuItem("&About", null, (_, _) => ShowAbout());
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(about);

        _menu.Items.AddRange(new ToolStripItem[] { file, edit, tools, help });
    }

    private void BuildToolbar()
    {
        var open = new ToolStripButton("Open...") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        open.Click += (_, _) => OpenWithDialog();

        _toolbarSave.Click += (_, _) => Save();
        _toolbarAdd.Click += (_, _) => AddRow();
        _toolbarDelete.Click += (_, _) => DeleteSelectedRows();

        var sql = new ToolStripButton("SQL query") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        sql.Click += (_, _) => ShowSqlQuery();

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            open, new ToolStripSeparator(),
            _toolbarSave, new ToolStripSeparator(),
            _toolbarAdd, _toolbarDelete, new ToolStripSeparator(),
            sql,
        });
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
    }

    private void BuildStatusBar()
    {
        _statusFile.Text = "No file open";
        _statusBar.Items.AddRange(new ToolStripItem[] { _statusFile, _statusSpring, _statusRows, _statusDirty });
    }

    private void BuildGridContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add row", null, (_, _) => AddRow());
        menu.Items.Add("Duplicate row", null, (_, _) => DuplicateRow());
        menu.Items.Add("Delete rows", null, (_, _) => DeleteSelectedRows());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_contextAddColumn);
        menu.Items.Add(_contextRemoveColumn);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy cell", null, (_, _) => CopyCell());

        _contextAddColumn.Click += (_, _) => AddColumn();
        _contextRemoveColumn.Click += (_, _) => RemoveColumn(_contextRemoveColumn.Tag as LiveColumn);

        // Right-clicking a column header aims Remove column at that column, not the current cell's.
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _rightClickedColumn = e.ColumnIndex >= 0 ? _grid.Columns[e.ColumnIndex].Tag as LiveColumn : null;
        };

        menu.Opening += (_, _) =>
        {
            LiveColumn? target = _rightClickedColumn ?? CurrentColumn();
            _rightClickedColumn = null;

            bool layout = _currentTable is { IsMapped: true, CanEditLayout: true };
            _contextAddColumn.Enabled = layout;
            _contextRemoveColumn.Tag = target;
            _contextRemoveColumn.Text = target is null ? "Remove column" : $"Remove column {target.Name}";
            _contextRemoveColumn.Enabled = layout && target is not null && _currentTable!.CanRemove(target);
        };

        _grid.ContextMenuStrip = menu;
    }

    // ---------------------------------------------------------------- opening

    private void OpenWithDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a ParamDB or a SQLite database",
            Filter = "ParamDB and SQLite files|paramdb*.db;*.sqlite;*.sqlite3;*.db3;*.db|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenPath(dialog.FileName);
    }

    private void OpenPath(string path)
    {
        if (!CloseSession(prompt: true))
            return;

        try
        {
            Cursor = Cursors.WaitCursor;

            _session = DetectKind(path) switch
            {
                FileKind.ParamDb => EditorSession.OpenParamDb(path, MakeTempSqlitePath(path)),
                FileKind.Sqlite => EditorSession.OpenSqlite(path),
                _ => throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' is neither a GTAR paramdb nor a SQLite database."),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Cursor = Cursors.Default;
            MessageBox.Show(this, e.Message, "Could not open the database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        PopulateTableList();
        UpdateChrome();

        LiveTable? first = _session.Database.Tables.FirstOrDefault(t => t.IsMapped && t.RowCount > 0)
                           ?? _session.Database.Tables.FirstOrDefault();
        if (first is not null)
            SelectTableInList(first);

        if (_session.Warnings.Count > 0)
            _statusSpring.Text = $"Opened with {_session.Warnings.Count} warning(s) - see Tools > Show load warnings.";
    }

    private enum FileKind { ParamDb, Sqlite, Unknown }

    private static FileKind DetectKind(string path)
    {
        Span<byte> header = stackalloc byte[16];

        using (FileStream stream = File.OpenRead(path))
        {
            int read = stream.Read(header);
            header = header[..read];
        }

        if (header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == GtarArchive.Magic)
            return FileKind.ParamDb;

        if (header.Length >= 15 && Encoding.ASCII.GetString(header[..15]) == "SQLite format 3")
            return FileKind.Sqlite;

        return FileKind.Unknown;
    }

    private static string MakeTempSqlitePath(string paramDbPath)
    {
        string directory = Path.Combine(Path.GetTempPath(), "GTParamDBEditor");
        Directory.CreateDirectory(directory);

        string stem = Path.GetFileNameWithoutExtension(paramDbPath);
        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}.sqlite");
    }

    private bool CloseSession(bool prompt)
    {
        if (_session is null)
            return true;

        if (prompt && !ConfirmDiscard())
            return false;

        // Dispose rather than Close: the SQL window cancels a user close and hides itself instead,
        // and it holds the LiveDatabase that is about to go away.
        _sqlForm?.Dispose();
        _sqlForm = null;

        ClearGrid();
        _currentTable = null;

        _tableList.Items.Clear();
        _tableFilter.Clear();
        _rowFilter.Clear();
        _gridHint.Text = "File > Open a paramdb.db to start editing.";
        _statusSpring.Text = "";

        _session.Dispose();
        _session = null;

        UpdateChrome();
        UpdateRowStatus();
        return true;
    }

    private bool ConfirmDiscard()
    {
        bool flushed = FlushPendingEdits();

        if (_session is null || (!_session.IsDirty && flushed))
            return true;

        DialogResult result = MessageBox.Show(
            this,
            "There are edits that have not been written to the game files yet.\n\nSave them now?",
            AppName,
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        return result switch
        {
            DialogResult.Yes => Save(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void Revert()
    {
        if (_session is null)
            return;

        if (_session.IsDirty || (_currentData is not null && LiveDatabase.HasPendingChanges(_currentData)))
        {
            DialogResult answer = MessageBox.Show(
                this,
                "Reloading throws away every edit made since the last save.\n\nContinue?",
                AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return;
        }

        // Drop the working copy so nothing gets flushed back on the way out.
        string path = _session.DisplayPath;
        ClearGrid();
        _currentTable = null;
        _session.IsDirty = false;

        OpenPath(path);
    }

    // ---------------------------------------------------------------- table list

    private void PopulateTableList()
    {
        _suppressTableChange = true;
        try
        {
            string? selected = _currentTable?.Name;
            string filter = _tableFilter.Text.Trim();

            _tableList.BeginUpdate();
            _tableList.Items.Clear();

            foreach (LiveTable table in _session?.Database.Tables ?? Enumerable.Empty<LiveTable>())
            {
                if (filter.Length > 0 && !table.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var item = new ListViewItem(table.Name) { Tag = table };
                item.SubItems.Add(table.RowCount.ToString("N0", CultureInfo.CurrentCulture));

                if (!table.IsMapped)
                {
                    item.ForeColor = SystemColors.GrayText;
                    item.ToolTipText = "No .headers mapping - kept byte for byte, not editable.";
                }
                else if (table.IsArchiveTable && table.SourceRowCount > 0 && table.RowStride != table.SourceElementSize)
                {
                    item.ForeColor = Color.FromArgb(150, 90, 0);
                    item.ToolTipText =
                        $"Rows {(table.RowStride > table.SourceElementSize ? "grow" : "shrink")} from " +
                        $"{table.SourceElementSize} to {table.RowStride} bytes when saved.";
                }
                else if (table.HasSizeMismatch)
                {
                    item.ForeColor = Color.FromArgb(150, 90, 0);
                    item.ToolTipText = $"The mapping covers {table.MappedRowSize} of {table.SourceElementSize} bytes per row.";
                }

                if (table.Name == selected)
                    item.Selected = true;

                _tableList.Items.Add(item);
            }

            _tableList.ShowItemToolTips = true;
            _tableList.EndUpdate();
        }
        finally
        {
            _suppressTableChange = false;
        }
    }

    private void SelectTableInList(LiveTable table)
    {
        foreach (ListViewItem item in _tableList.Items)
        {
            if (!ReferenceEquals(item.Tag, table))
                continue;

            item.Selected = true;
            item.EnsureVisible();
            return;
        }
    }

    private void OnTableSelected(object? sender, EventArgs e)
    {
        if (_suppressTableChange || _session is null)
            return;

        if (_tableList.SelectedItems.Count == 0)
            return;

        if (_tableList.SelectedItems[0].Tag is not LiveTable table || ReferenceEquals(table, _currentTable))
            return;

        FlushPendingEdits();
        ShowTable(table);
    }

    private void ShowTable(LiveTable table)
    {
        if (_session is null)
            return;

        // The filter is an expression over the outgoing table's columns, so it has to go before the
        // rebind - a DataView rejects a filter naming columns its table does not have.
        _rowFilter.Text = "";
        _binding.Filter = null;

        if (!table.IsMapped)
        {
            ClearGrid();
            _currentTable = table;

            _gridHint.Text =
                $"'{table.Name}' has no column mapping.\r\n\r\n" +
                $"Its {table.SourceRowCount:N0} rows of {table.SourceElementSize} bytes are kept exactly as they are\r\n" +
                "and written back untouched when you save.";

            UpdateRowStatus();
            UpdateCommandStates();
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;

            DataTable data = _session.Database.LoadTable(table);

            // Detach before touching the columns so no stale cell-formatting callback can run
            // against the half-rebuilt grid.
            _binding.DataSource = null;
            BuildGridColumns(table);
            _binding.DataSource = data;

            _currentData?.Dispose();

            // Committed together. If these two ever disagreed, the next flush would write one
            // table's rows into another - and several tables here share an identical schema, so
            // that would land silently rather than erroring.
            _currentData = data;
            _currentTable = table;

            _grid.Visible = true;
            _gridHint.Visible = false;
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
        {
            ClearGrid();
            _currentTable = null;
            _gridHint.Text = $"'{table.Name}' could not be loaded.";

            MessageBox.Show(this, e.Message, "Could not load the table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        UpdateRowStatus();
        UpdateCommandStates();
    }

    /// <summary>Drops the working copy and everything on screen that belongs to it.</summary>
    private void ClearGrid()
    {
        _binding.DataSource = null;
        _binding.Filter = null;
        _grid.Columns.Clear();

        _currentData?.Dispose();
        _currentData = null;

        _grid.Visible = false;
        _gridHint.Visible = true;
    }

    /// <summary>
    /// Builds one grid column per mapped column, in header order. The DataTable also carries the
    /// SQLite rowid, which deliberately gets no column here - it is bookkeeping for the flush, not
    /// something to show, and a hidden column ahead of the frozen label is what made the grid throw.
    /// </summary>
    private void BuildGridColumns(LiveTable table)
    {
        _grid.Columns.Clear();

        foreach (LiveColumn info in table.Columns)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = info.Name,
                DataPropertyName = info.Name,
                HeaderText = info.Name,
                ValueType = info.ClrType,
                SortMode = DataGridViewColumnSortMode.Automatic,
                ToolTipText = BuildColumnTooltip(info),
                Tag = info,
                MinimumWidth = 40,
            };

            if (!info.IsText)
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            if (info.Type == DBColumnType.Id)
                column.DefaultCellStyle.ForeColor = Color.FromArgb(0, 70, 140);

            _grid.Columns.Add(column);
        }

        // Keep the label in view while scrolling through wide tables. Only safe because it is the
        // very first column: a frozen column with anything unfrozen ahead of it is a state the grid
        // rejects the next time a column is added.
        if (table.PrimaryKeyColumn is not null && _grid.Columns.Count > 0)
            _grid.Columns[0].Frozen = true;
    }

    private static string BuildColumnTooltip(LiveColumn column)
    {
        var text = new StringBuilder();
        text.Append(column.Name).Append(" - ").Append(column.Type).Append(" at offset 0x").Append(column.Offset.ToString("X"));

        if (!string.IsNullOrEmpty(column.Documentation))
            text.AppendLine().AppendLine().Append(column.Documentation);

        return text.ToString();
    }

    // ---------------------------------------------------------------- editing

    private void MarkDirty()
    {
        if (_session is null)
            return;

        _session.IsDirty = true;
        UpdateChrome();
    }

    /// <summary>
    /// Pushes grid edits into the live SQLite database. Returns false if they could not be applied,
    /// which has to stop a save - writing the game files without them would lose them for good.
    /// </summary>
    private bool FlushPendingEdits()
    {
        if (_session is null || _currentTable is null || _currentData is null)
            return true;

        try
        {
            _grid.EndEdit();
            _binding.EndEdit();

            if (_session.Database.Flush(_currentTable, _currentData) > 0)
            {
                _session.IsDirty = true;
                RefreshTableCounts();
            }

            return true;
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            MessageBox.Show(this, e.Message, "Could not apply the edits", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void AddRow()
    {
        if (_currentTable is null || _currentData is null)
            return;

        _rowFilter.Text = "";

        DataRow row = _currentData.NewRow();
        foreach (LiveColumn column in _currentTable.Columns)
            row[column.Name] = column.IsText ? "" : 0L;

        if (_currentTable.PrimaryKeyColumn is { } key)
            row[key.Name] = MakeUniqueLabel(key.Name, "NEW_ROW");

        _currentData.Rows.Add(row);
        MarkDirty();

        FocusLastRow();
        UpdateRowStatus();
    }

    private void FocusLastRow()
    {
        if (_grid.RowCount == 0)
            return;

        int column = _grid.CurrentCell?.ColumnIndex ?? -1;
        if (column < 0 || !_grid.Columns[column].Visible)
            column = _grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Visible)?.Index ?? -1;

        if (column < 0)
            return;

        _grid.CurrentCell = _grid.Rows[_grid.RowCount - 1].Cells[column];
    }

    private void DuplicateRow()
    {
        if (_currentTable is null || _currentData is null || _grid.CurrentRow is null)
            return;

        if (_grid.CurrentRow.DataBoundItem is not DataRowView source)
            return;

        _rowFilter.Text = "";

        DataRow row = _currentData.NewRow();
        foreach (LiveColumn column in _currentTable.Columns)
            row[column.Name] = source.Row[column.Name];

        if (_currentTable.PrimaryKeyColumn is { } key)
            row[key.Name] = MakeUniqueLabel(key.Name, Convert.ToString(source.Row[key.Name], CultureInfo.InvariantCulture) ?? "NEW_ROW");

        _currentData.Rows.Add(row);
        MarkDirty();

        FocusLastRow();
        UpdateRowStatus();
    }

    private string MakeUniqueLabel(string columnName, string stem)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in _currentData!.Rows)
        {
            if (row.RowState != DataRowState.Deleted && row[columnName] is string existing)
                taken.Add(existing);
        }

        for (int i = 1; ; i++)
        {
            string candidate = $"{stem}_{i}";
            if (taken.Add(candidate))
                return candidate;
        }
    }

    private void DeleteSelectedRows()
    {
        if (_currentData is null || _grid.SelectedCells.Count == 0)
            return;

        var rows = new HashSet<DataRow>();
        foreach (DataGridViewCell cell in _grid.SelectedCells)
        {
            if (_grid.Rows[cell.RowIndex].DataBoundItem is DataRowView view)
                rows.Add(view.Row);
        }

        if (rows.Count == 0)
            return;

        string question = rows.Count == 1 ? "Delete the selected row?" : $"Delete {rows.Count} selected rows?";
        if (MessageBox.Show(this, question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (DataRow row in rows)
            row.Delete();

        MarkDirty();
        UpdateRowStatus();
    }

    private void CopyCell()
    {
        string text = Convert.ToString(_grid.CurrentCell?.Value, CultureInfo.CurrentCulture) ?? "";

        if (text.Length > 0)
            Clipboard.SetText(text);
        else
            Clipboard.Clear();
    }

    private LiveColumn? CurrentColumn() => _grid.CurrentCell?.OwningColumn?.Tag as LiveColumn;

    private void AddColumn()
    {
        if (_session is null || _currentTable is not { IsMapped: true, CanEditLayout: true } table || !FlushPendingEdits())
            return;

        using var dialog = new AddColumnDialog(table);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        int rowSize = table.RowStride;
        try
        {
            _session.Database.AddColumn(table, dialog.ColumnName, dialog.ColumnType, dialog.ColumnOffset);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            MessageBox.Show(this, e.Message, "Could not add the column", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AfterLayoutChange(table, $"Added {dialog.ColumnName} to {table.Name}.", rowSize, dialog.ColumnName);
    }

    private void RemoveColumn(LiveColumn? column)
    {
        if (_session is null || column is null || _currentTable is not { IsMapped: true, CanEditLayout: true } table)
            return;

        if (!table.CanRemove(column))
        {
            MessageBox.Show(this, $"{column.Name} is the label every row is found by, so it cannot be removed.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!FlushPendingEdits())
            return;

        int rowSize = table.RowStride;
        int after = table.RowSizeWithout(column);
        string effect = after < rowSize
            ? $"Rows shrink from {rowSize} to {after} bytes when you save, and what the column holds is lost."
            : (column.Size == 1 ? $"Its byte at 0x{column.Offset:X} stays" : $"Its {column.Size} bytes at 0x{column.Offset:X} stay") +
              " in every row as the file has them, so the columns after it keep their offsets.";

        if (MessageBox.Show(this, $"Remove {column.Name} from {table.Name}?\n\n{effect}", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            _session.Database.RemoveColumn(table, column);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            MessageBox.Show(this, e.Message, "Could not remove the column", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AfterLayoutChange(table, $"Removed {column.Name} from {table.Name}.", rowSize);
    }

    /// <summary>Shows a table again after its columns changed, and says what that does to its rows.</summary>
    private void AfterLayoutChange(LiveTable table, string message, int rowSizeBefore, string? focusColumn = null)
    {
        MarkDirty();
        PopulateTableList();
        ShowTable(table);

        if (table.RowStride != rowSizeBefore)
            message += $" Rows {(table.RowStride > rowSizeBefore ? "grow" : "shrink")} from {rowSizeBefore} to {table.RowStride} bytes when you save.";

        _statusSpring.Text = message;

        if (focusColumn is not null && _grid.Columns[focusColumn] is { } column && _grid.RowCount > 0)
            _grid.CurrentCell = _grid.Rows[0].Cells[column.Index];
    }

    private void ApplyRowFilter()
    {
        if (_currentData is null)
            return;

        string term = _rowFilter.Text.Trim();

        try
        {
            _binding.Filter = term.Length == 0 ? null : BuildFilterExpression(term);
        }
        catch (Exception e) when (e is EvaluateException or SyntaxErrorException)
        {
            _binding.Filter = null;
        }

        UpdateRowStatus();
    }

    private string? BuildFilterExpression(string term)
    {
        if (_currentTable is null)
            return null;

        string escaped = term
            .Replace("'", "''")
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("*", "[*]");

        IEnumerable<string> clauses = _currentTable.Columns
            .Select(c => $"CONVERT([{c.Name}], 'System.String') LIKE '%{escaped}%'");

        return string.Join(" OR ", clauses);
    }

    private void OnGridDataError(object? sender, DataGridViewDataErrorEventArgs e)
    {
        e.ThrowException = false;
        e.Cancel = true;

        LiveColumn? column = e.ColumnIndex >= 0 && e.ColumnIndex < _grid.Columns.Count
            ? _grid.Columns[e.ColumnIndex].Tag as LiveColumn
            : null;
        string expected = column is null
            ? "a valid value"
            : column.IsText ? "text" : column.Type is DBColumnType.Float or DBColumnType.Double ? "a number" : "a whole number";

        _statusSpring.Text = $"That cell needs {expected}.";
        SystemSounds.Beep.Play();
    }

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        // Doubles holding a float show every bit of the conversion (0.30000001192...); show the float.
        if (e.Value is not double value)
            return;

        if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count)
            return;

        if (_grid.Columns[e.ColumnIndex].Tag is not LiveColumn { Type: DBColumnType.Float })
            return;

        e.Value = ((float)value).ToString(CultureInfo.CurrentCulture);
        e.FormattingApplied = true;
    }

    // ---------------------------------------------------------------- saving

    private bool Save()
    {
        if (_session is null)
            return true;

        if (_session.Target is null)
        {
            return SaveAs();
        }

        return SaveTo(_session.Target);
    }

    private bool SaveAs()
    {
        if (_session is null)
            return true;

        using var dialog = new SaveFileDialog
        {
            Title = "Write the game files next to...",
            Filter = "ParamDB|paramdb*.db",
            FileName = _session.Target is { } current
                ? Path.GetFileName(current.ParamDbOut)
                : string.IsNullOrEmpty(_session.Database.Meta.Suffix) ? "paramdb.db" : $"paramdb_{_session.Database.Meta.Suffix}.db",
            InitialDirectory = _session.Target?.DirectoryPath ?? "",
            OverwritePrompt = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        ParamDbPaths target = ParamDbPaths.FromParamDbFile(dialog.FileName);
        if (!SaveTo(target))
            return false;

        // Carry on working with what was just written, so Save and Reload both mean the new file
        // rather than quietly reverting to the folder this was opened from.
        _session.Target = target;
        _session.DisplayPath = target.ParamDbOut;

        UpdateChrome();
        return true;
    }

    private bool SaveTo(ParamDbPaths target)
    {
        if (_session is null)
            return true;

        if (!FlushPendingEdits())
            return false;

        SaveReport report;
        try
        {
            Cursor = Cursors.WaitCursor;
            report = ParamDbWriter.Save(_session.Database, target, _backupItem.Checked);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, e.Message, "Could not write the game files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (!report.Succeeded)
        {
            using var dialog = new ListDialog(
                "Nothing was written",
                "These values cannot be stored in the game's format. Fix them and save again - " +
                "the game files on disk have not been touched.",
                report.Issues.Select(i => i.ToString()));
            dialog.ShowDialog(this);
            return false;
        }

        _session.IsDirty = false;
        UpdateChrome();

        var summary = new StringBuilder();
        summary.Append(report.WrittenFiles.Count).Append(" file(s) written to ").Append(target.DirectoryPath);
        if (report.Backups.Count > 0)
            summary.Append(" (").Append(report.Backups.Count).Append(" backup(s) created)");

        if (report.LayoutFiles.Count > 0)
            summary.Append("; column layout saved to ").Append(string.Join(", ", report.LayoutFiles));

        _statusSpring.Text = summary.ToString();

        if (report.Warnings.Count > 0)
        {
            using var dialog = new ListDialog(
                "Saved, with notes",
                $"The game files in {target.DirectoryPath} were updated. Worth a look:",
                report.Warnings);
            dialog.ShowDialog(this);
        }

        return true;
    }

    private void ExportSqlite()
    {
        if (_session is null)
            return;

        FlushPendingEdits();

        using var dialog = new SaveFileDialog
        {
            Title = "Export a copy of the live database",
            Filter = "SQLite database|*.sqlite",
            FileName = Path.GetFileNameWithoutExtension(_session.DisplayPath) + ".sqlite",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (File.Exists(dialog.FileName))
                File.Delete(dialog.FileName);

            _session.Database.CopyTo(dialog.FileName);
            _statusSpring.Text = $"Exported to {dialog.FileName}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, e.Message, "Could not export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- extras

    private void ShowSqlQuery()
    {
        if (_session is null)
            return;

        FlushPendingEdits();

        if (_sqlForm is null || _sqlForm.IsDisposed)
        {
            _sqlForm = new SqlQueryForm(_session.Database);

            // The window can sit open while the grid is edited, so flush on every statement rather
            // than only when the window is summoned.
            _sqlForm.BeforeExecute += (_, e) => e.Cancel = !FlushPendingEdits();

            _sqlForm.DatabaseChanged += (_, _) =>
            {
                MarkDirty();
                RefreshTableCounts();
                if (_currentTable is not null)
                    ShowTable(_currentTable);
            };
        }

        _sqlForm.Show(this);
        _sqlForm.BringToFront();
    }

    private void ShowWarnings()
    {
        IEnumerable<string> warnings = _session?.Warnings ?? Enumerable.Empty<string>();

        using var dialog = new ListDialog(
            "Load warnings",
            warnings.Any()
                ? "Noted while reading the database:"
                : "Nothing to report - everything mapped cleanly.",
            warnings);

        dialog.ShowDialog(this);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            $"{AppName}\n\n" +
            "Opens a Gran Turismo 3 or Gran Turismo Concept ParamDB as a live SQLite database, lets\n" +
            "you edit it, and writes the game files back out when you save.\n\n",
            $"About {AppName}",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void RefreshTableCounts()
    {
        _session?.Database.RefreshRowCounts();

        foreach (ListViewItem item in _tableList.Items)
        {
            if (item.Tag is LiveTable table)
                item.SubItems[1].Text = table.RowCount.ToString("N0", CultureInfo.CurrentCulture);
        }
    }

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        string dirty = _session?.IsDirty == true ? " *" : "";
        Text = _session is null ? AppName : $"{Path.GetFileName(_session.DisplayPath)}{dirty} - {AppName}";

        _statusFile.Text = _session is null ? "No file open" : $"{_session.DisplayPath}  ({_session.Database.Game.DisplayName})";
        _statusDirty.Text = _session is null ? "" : _session.IsDirty ? "Unsaved changes" : "Saved";
        _statusDirty.ForeColor = _session?.IsDirty == true ? Color.FromArgb(160, 60, 0) : SystemColors.ControlText;

        UpdateCommandStates();
    }

    private void UpdateCommandStates()
    {
        bool open = _session is not null;
        bool editable = open && _currentTable?.IsMapped == true;

        _saveItem.Enabled = open;
        _saveAsItem.Enabled = open;
        _exportItem.Enabled = open;
        _revertItem.Enabled = open;
        _closeItem.Enabled = open;
        _sqlItem.Enabled = open;
        _warningsItem.Enabled = open;
        _toolbarSave.Enabled = open;

        _addRowItem.Enabled = editable;
        _duplicateRowItem.Enabled = editable;
        _deleteRowItem.Enabled = editable;
        _toolbarAdd.Enabled = editable;
        _toolbarDelete.Enabled = editable;

        _grid.ReadOnly = !editable;
        _rowFilter.Enabled = editable;

        UpdateColumnCommands();
    }

    private void UpdateColumnCommands()
    {
        bool layout = _session is not null && _currentTable is { IsMapped: true, CanEditLayout: true };

        _addColumnItem.Enabled = layout;
        _removeColumnItem.Enabled = layout && CurrentColumn() is { } column && _currentTable!.CanRemove(column);
    }

    private void UpdateRowStatus()
    {
        if (_currentTable is null)
        {
            _statusRows.Text = "";
            return;
        }

        int shown = _currentData is null ? _currentTable.SourceRowCount : _binding.Count;
        int total = _currentData?.Rows.Count ?? _currentTable.SourceRowCount;

        string rows = shown == total
            ? $"{total:N0} rows"
            : $"{shown:N0} of {total:N0} rows";

        string cell = "";
        if (_grid.CurrentCell?.OwningColumn?.Tag is LiveColumn column)
            cell = $"   |   {column.Name} ({column.Type})";

        _statusRows.Text = $"{_currentTable.Name}: {rows}{cell}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!CloseSession(prompt: true))
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Only meaningful once the container has a real size.
        _split.SplitterDistance = 270;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (!string.IsNullOrWhiteSpace(_initialPath) && File.Exists(_initialPath))
            OpenPath(_initialPath);
    }
}
