using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FinanceTracker.Models;

namespace FinanceTracker.Views
{
    // Карточка покупки со свайпом справа налево. Используется только на Android.
    // При достаточно сильном свайпе открываются кнопки удаления и изменения.
    public partial class SwipeExpenseRow : UserControl
    {
        public const double ActionWidth = 84;
        private const double DragStartThreshold = 8;
        private const double VerticalForfeitThreshold = 12;
        private const double SnapRatio = 0.4;
        private const double SnapDurationMs = 160;
        private const double FlingThreshold = 0.4;

        private bool _dragging;
        private double _startX;
        private double _startY;
        private double _startTranslate;
        private double _velocity;
        private long _lastMoveTicks;
        private double _lastMoveX;
        private bool _tapCandidate;
        private double _maxReveal;
        private DispatcherTimer? _snapTimer;
        private Stopwatch? _snapWatch;
        private TranslateTransform? _translate;

        public event EventHandler<ExpenseItem>? EditRequested;
        public event EventHandler<ExpenseItem>? DeleteRequested;
        public event EventHandler? SwipeBegan;
        public event EventHandler<ExpenseItem>? SelectionToggled;

        // Ряды переиспользуются VirtualizingStackPanel, поэтому режим выделения
        // не храним на самом ряду, а спрашиваем у владельца через делегат.
        public Func<bool>? SelectionModeProvider { get; set; }

        public SwipeExpenseRow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private ExpenseItem? Item => DataContext as ExpenseItem;

        private TranslateTransform Translate => _translate ??= (TranslateTransform)Card.RenderTransform!;

        private double CurrentTranslate => Translate.X;

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _snapTimer?.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _snapTimer?.Stop();
            _dragging = false;
            _tapCandidate = false;
            Translate.X = 0;
        }

        // Закрывает карточку (используется MainView после открытия другой карточки).
        public void CloseRow()
        {
            _snapTimer?.Stop();
            if (CurrentTranslate <= -2)
                Snap(false);
        }

        private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _snapTimer?.Stop();
            var point = e.GetCurrentPoint(this);
            bool isTouch = e.Pointer.Type == PointerType.Touch;
            if (!isTouch && (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed))
                return;

            _startX = point.Position.X;
            _startY = point.Position.Y;
            _startTranslate = CurrentTranslate;
            _velocity = 0;
            _lastMoveTicks = 0;
            _lastMoveX = _startX;
            _maxReveal = Math.Max(0, -_startTranslate);
            _dragging = false;
            _tapCandidate = true;
        }

        private void OnCardPointerMoved(object? sender, PointerEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            double dx = point.Position.X - _startX;
            double dy = point.Position.Y - _startY;

            if (!_dragging)
            {
                // Палец ушёл по вертикали дальше порога — это прокрутка списка.
                // ScrollViewer уже захватил жест, не пытаемся открыть кнопки.
                if (Math.Abs(dy) >= VerticalForfeitThreshold)
                {
                    _tapCandidate = false;
                    return;
                }
                if (Math.Abs(dx) < DragStartThreshold)
                {
                    if (Math.Abs(dy) >= DragStartThreshold)
                        _tapCandidate = false;
                    return;
                }
                if (Math.Abs(dx) <= Math.Abs(dy))
                {
                    // Движение больше похоже на вертикальную прокрутку списка —
                    // не считаем жестом свайпа и не захватываем указатель.
                    _tapCandidate = false;
                    return;
                }

                _dragging = true;
                _tapCandidate = false;
                _snapTimer?.Stop();

                // Начался реальный горизонтальный свайп карточки (dx >= 8, dx > dy).
                // Запрещаем ScrollGestureRecognizer списка участвовать в этом жесте.
                // Захват держим на самой Card (не на UserControl!): на Android платформа
                // изначально захватила Card, а Capture(this) на строке увёл бы все
                // последующие MOVE/UP в обход Card и прервал свайп.
                e.PreventGestureRecognition();
                e.Pointer.Capture(Card);
                SwipeBegan?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }

            double target = Math.Clamp(_startTranslate + dx, -ActionWidth, 0);
            if (Math.Abs(target - CurrentTranslate) > 0.5)
                Translate.X = target;
            _maxReveal = Math.Max(_maxReveal, -CurrentTranslate);

            long now = Environment.TickCount64;
            double dt = now - _lastMoveTicks;
            if (dt >= 4 && Math.Abs(point.Position.X - _lastMoveX) >= 1)
                _velocity = (point.Position.X - _lastMoveX) / dt;
            _lastMoveTicks = now;
            _lastMoveX = point.Position.X;

            e.Handled = true;
        }

        private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            bool wasDragging = _dragging;
            _dragging = false;

            if (e.Pointer.Captured == this)
                e.Pointer.Capture(null);

            if (wasDragging)
            {
                Snap(DecideDirectionAfterRelease());
                e.Handled = true;
            }
            else if (_tapCandidate)
            {
                _tapCandidate = false;
                if ((SelectionModeProvider?.Invoke() ?? false) && Item is { } toggleItem)
                {
                    toggleItem.IsSelected = !toggleItem.IsSelected;
                    if (CurrentTranslate <= -2)
                        Snap(false);
                    SelectionToggled?.Invoke(this, toggleItem);
                    e.Handled = true;
                }
                else if (CurrentTranslate <= -2)
                {
                    Snap(false);
                    e.Handled = true;
                }
            }
        }

        private void OnCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            bool wasDragging = _dragging;
            _dragging = false;
            _tapCandidate = false;

            if (wasDragging)
                Snap(DecideDirectionAfterRelease());
        }

        private bool DecideDirectionAfterRelease()
        {
            // Резкий свайп влево — всегда открываем, резкий вправо — закрываем.
            if (_velocity != 0 && Math.Abs(_velocity) >= FlingThreshold)
                return _velocity < 0;

            double reveal = _startTranslate - CurrentTranslate;
            double absoluteOpen = Math.Max(0, -CurrentTranslate);

            // Пользователь явно закрыл открытую карточку (дотянул вправо почти до нуля).
            if (absoluteOpen < ActionWidth * SnapRatio && reveal <= 0)
                return false;

            // Сильный свайп влево — даже если палец перед отпусканием ушёл назад,
            // карточка остаётся открытой.
            return _maxReveal >= ActionWidth * SnapRatio;
        }

        private void OnDeleteButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Item is { } item)
            {
                _snapTimer?.Stop();
                Snap(false);
                DeleteRequested?.Invoke(this, item);
            }
        }

        private void OnEditButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Item is { } item)
            {
                _snapTimer?.Stop();
                Snap(false);
                EditRequested?.Invoke(this, item);
            }
        }

        private void Snap(bool open)
        {
            double from = CurrentTranslate;
            double to = open ? -ActionWidth : 0;

            if (Math.Abs(from - to) < 0.5)
            {
                Translate.X = to;
                return;
            }

            _snapTimer?.Stop();
            _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _snapWatch = Stopwatch.StartNew();
            _snapTimer.Tick += (s, ts) => SnapTick(from, to);
            _snapTimer.Start();
        }

        private void SnapTick(double from, double to)
        {
            double t = _snapWatch!.Elapsed.TotalMilliseconds / SnapDurationMs;
            if (t >= 1.0)
            {
                _snapTimer!.Stop();
                Translate.X = to;
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);
            Translate.X = from + (to - from) * eased;
        }
    }
}
