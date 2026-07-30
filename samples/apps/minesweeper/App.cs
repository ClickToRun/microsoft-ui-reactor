using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Minesweeper.Components;
using Minesweeper.Components.Dialogs;
using Minesweeper.Game;
using Minesweeper.Persistence;
using static Microsoft.UI.Reactor.Factories;

namespace Minesweeper;

/// <summary>
/// Root component. Owns the reducer-driven game state, the elapsed-seconds
/// timer, persisted high scores, and all dialogs. Adapts colors to the
/// current Windows theme via <see cref="Theme"/> tokens and
/// <see cref="UseIsDarkTheme"/>.
/// All state transitions live in <see cref="AppReducer"/> so they're unit-testable.
/// </summary>
public sealed class MinesweeperApp : Component
{
    static readonly HighScoreStore Store = new();
    static readonly Random SharedRng = new();

    public override Element Render()
    {
        var (state, dispatch) = UseReducer<AppState, AppAction>(
            (s, a) => AppReducer.Reduce(s, a, SharedRng, scores => Store.Save(scores)),
            AppReducer.Initial(Difficulty.Beginner, Store.Load()));
        var isDark = UseIsDarkTheme();
        var timerRef = UseRef<Microsoft.UI.Xaml.DispatcherTimer?>(null);

        // Cell size scales mildly with difficulty so the Expert board (16×30)
        // still fits in a reasonable window — but never so small as to be
        // unclickable. Beginner is the largest, Expert the smallest.
        var cellSize = state.Board.Difficulty.Kind switch
        {
            DifficultyKind.Beginner => 36.0,
            DifficultyKind.Intermediate => 30.0,
            DifficultyKind.Expert => 26.0,
            _ => state.Board.Columns >= 20 ? 26.0 : (state.Board.Columns >= 14 ? 30.0 : 36.0),
        };

        // Timer effect: ticks every second while the game is in Playing phase
        // and no modal dialog is open (so the timer pauses while reading
        // high scores or configuring a custom board). Uses a ref-based
        // pattern (mirroring WordPuzzle) so reset → first-click reliably
        // restarts the timer rather than relying on UseEffect dependency
        // diffing on a derived bool.
        var pauseTimer = state.ShowHighScores || state.ShowCustomDialog || state.ShowNewBest;
        var shouldTick = state.Board.Phase == GamePhase.Playing && !pauseTimer;
        UseEffect(() =>
        {
            if (shouldTick && timerRef.Current == null)
            {
                var t = new Microsoft.UI.Xaml.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                t.Tick += (_, _) => dispatch(new TickAction());
                t.Start();
                timerRef.Current = t;
            }
            else if (!shouldTick && timerRef.Current != null)
            {
                timerRef.Current.Stop();
                timerRef.Current = null;
            }
        }, shouldTick);

        // ── Action handlers wired to the reducer ───────────────────────
        void OnReveal(int r, int c)
        {
            dispatch(new RevealAction(r, c));
        }
        void OnFlag(int r, int c) => dispatch(new FlagAction(r, c));
        void OnChord(int r, int c) => dispatch(new ChordAction(r, c));
        void OnBeginPreview(int r, int c) => dispatch(new BeginChordPreviewAction(r, c));
        void OnEndPreview(bool commit) => dispatch(new EndChordPreviewAction(commit));
        void OnReset() => dispatch(new ResetAction(state.Board.Difficulty));
        void OnNewGame(Difficulty d) => dispatch(new ResetAction(d));

        // ── Side-effect: when a game is won, check & open the new-best dialog ──
        UseEffect(() =>
        {
            if (state.Board.Phase != GamePhase.Won) return () => { };
            if (HighScoreStore.IsNewRecord(state.Scores, state.Board.Difficulty.Kind, state.ElapsedSeconds))
                dispatch(new ShowNewBestAction());
            return () => { };
        }, state.Board.Phase, state.Board.Difficulty.Kind);

        // ── Layout ─────────────────────────────────────────────────────
        var statusPanel = StatusPanel.Render(
            minesRemaining: state.Board.MinesRemaining,
            elapsedSeconds: state.ElapsedSeconds,
            phase: state.Board.Phase,
            isPressing: state.IsPressing,
            onReset: OnReset);

        var boardProps = new BoardViewProps(
            Board: state.Board,
            CellSize: cellSize,
            IsDarkTheme: isDark,
            ChordPreview: state.ChordPreview,
            OnReveal: OnReveal,
            OnFlag: OnFlag,
            OnChord: OnChord,
            OnBeginChordPreview: OnBeginPreview,
            OnEndChordPreview: OnEndPreview);

        var menuBar = MenuBar([
            Menu("Game",
                MenuItem("New", OnReset),
                MenuSeparator(),
                MenuItem("Beginner", () => OnNewGame(Difficulty.Beginner)),
                MenuItem("Intermediate", () => OnNewGame(Difficulty.Intermediate)),
                MenuItem("Expert", () => OnNewGame(Difficulty.Expert)),
                MenuItem("Custom…", () => dispatch(new OpenCustomDialogAction())),
                MenuSeparator(),
                MenuItem("High scores…", () => dispatch(new OpenHighScoresAction()))
            ),
        ]);

        // Natural board size at the configured cell size — used to cap how
        // big the board can render so the tiles never zoom past 1x. The
        // +2 accounts for BoardView's 1-px frame on each side.
        var naturalBoardWidth = cellSize * state.Board.Columns + 2;
        var naturalBoardHeight = cellSize * state.Board.Rows + 2;

        // Viewbox now works correctly because the outer Grid (see bottom of
        // Render) gives this Star row a bounded height. Stretch=Uniform
        // scales the board to fit whatever both-axis-bounded space we get,
        // preserving the cell aspect ratio. MaxWidth/MaxHeight cap the
        // upscale so cells never exceed their natural pixel size in big
        // windows. HAlign=Center centers the resulting (possibly capped)
        // Viewbox horizontally; VAlign=Center vertically.
        var boardScaled = Viewbox(
            Component<BoardView, BoardViewProps>(boardProps)
        )
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center)
        .MaxWidth(naturalBoardWidth)
        .MaxHeight(naturalBoardHeight)
        .Set(vb =>
        {
            vb.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
        });

        var titleSubtitle = state.Board.Phase switch
        {
            GamePhase.Won => $"You won in {state.ElapsedSeconds}s!",
            GamePhase.Lost => "Boom! Click 🙂 to try again.",
            GamePhase.Playing => $"{state.Board.Difficulty.DisplayName} — {state.ElapsedSeconds}s",
            _ => state.Board.Difficulty.DisplayName,
        };

        // ── Modal overlays ────────────────────────────────────────────
        // Built as content factories so they layer on top of the board via
        // a Grid stack rather than going through WinUI's ContentDialog
        // (which silently fails when XamlRoot can't be inferred from
        // freshly-mounted content).
        var highScoresOverlay = ModalOverlay.Render(
            isOpen: state.ShowHighScores,
            title: "High Scores",
            body: Components.Dialogs.DialogContent.HighScores(state.Scores),
            buttons: [
                ("Reset all", () => dispatch(new ResetHighScoresAction()), false),
                ("Close", () => dispatch(new CloseHighScoresAction()), true),
            ]);

        var (customRows, setCustomRows) = UseState("9");
        var (customCols, setCustomCols) = UseState("9");
        var (customMines, setCustomMines) = UseState("10");
        bool tryParseCustom(out int r, out int c, out int m) =>
            int.TryParse(customRows, out r) & int.TryParse(customCols, out c) & int.TryParse(customMines, out m);
        var customError = tryParseCustom(out var cr, out var cc, out var cm)
            ? (Difficulty.IsValidCustom(cr, cc, cm, out var msg) ? null : msg)
            : "All three fields must be numbers.";
        var customOverlay = ModalOverlay.Render(
            isOpen: state.ShowCustomDialog,
            title: "Custom board",
            body: Components.Dialogs.DialogContent.CustomBoard(
                customRows, setCustomRows,
                customCols, setCustomCols,
                customMines, setCustomMines,
                customError),
            buttons: [
                ("Cancel", () => dispatch(new CloseCustomDialogAction()), false),
                ("Start", () =>
                {
                    if (tryParseCustom(out var r, out var c, out var m)
                        && Difficulty.IsValidCustom(r, c, m, out _))
                        dispatch(new ApplyCustomAction(r, c, m));
                }, true),
            ]);

        var newBestOverlay = ModalOverlay.Render(
            isOpen: state.ShowNewBest,
            title: "New best time!",
            body: Components.Dialogs.DialogContent.NewBest(
                state.Board.Difficulty.Kind,
                state.ElapsedSeconds),
            buttons: [
                ("Close", () => dispatch(new CloseNewBestAction()), true),
            ]);

        // Main play area: menu bar + status panel sit at their natural size
        // (Auto rows); the board takes whatever vertical space is left
        // (Star row) and the Viewbox scales it to fit. Bounded rows mean
        // the board never overflows the window the way an unbounded VStack
        // could.
        var playArea = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            menuBar.Grid(row: 0, column: 0),
            statusPanel.HAlign(HorizontalAlignment.Center).Margin(24, 16, 24, 8).Grid(row: 1, column: 0),
            boardScaled.Margin(24, 8, 24, 24).Grid(row: 2, column: 0)
        );

        // The main content + overlays stack via a Grid so dialogs sit above
        // the board without being affected by VStack's vertical flow.
        var mainPlusOverlays = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            playArea.Grid(row: 0, column: 0),
            highScoresOverlay.Grid(row: 0, column: 0),
            customOverlay.Grid(row: 0, column: 0),
            newBestOverlay.Grid(row: 0, column: 0)
        );

        // Outer container: must be a Grid (not VStack/StackPanel) so the
        // mainPlusOverlays Star row gets a *bounded* height — otherwise
        // any Viewbox inside has infinite vertical space and won't shrink
        // when the window is shortened.
        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            (TitleBar("Reactor Minesweeper") with
            {
                Subtitle = titleSubtitle,
            }).Grid(row: 0, column: 0),
            mainPlusOverlays.Grid(row: 1, column: 0)
        ).Backdrop(BackdropKind.Mica);
    }

    // ── Reducer & actions live in Game/AppReducer.cs (testable, no UI deps) ──
}
