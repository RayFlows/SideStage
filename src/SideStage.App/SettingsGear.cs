/*
 * 文件名称：SettingsGear.cs
 * 文件职责：用黄铜齿轮打开预览内的高级设置，区分单击与拖动，管理四边吸附和空闲收起。
 * 坐标约定：位置相对预览工作区；沿边的位置按比例保存，窗口缩放后仍贴在同一条边。
 * 输入边界：仅图片的可见轮廓参与命中测试，轮廓以外的输入交给下方远程画面。
 */
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SideStage.App;

internal sealed class SettingsGear : Control
{
    private readonly Bitmap _source;
    private Bitmap? _face;
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _idle = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };
    private DockStyle _edge = DockStyle.Right;
    private double _fraction = .06;
    private Point _press, _offset, _target;
    private bool _held, _dragging, _tucked;

    /// <summary>读取随宿主嵌入的素材，建立鼠标、键盘和计时器共用的设置入口。</summary>
    internal SettingsGear()
    {
        using Stream stream = typeof(SettingsGear).Assembly.GetManifestResourceStream("SideStage.App.SettingsGear.png")
            ?? throw new InvalidDataException("缺少黄铜齿轮设置按钮图片。");
        using Bitmap image = new(stream);
        _source = new Bitmap(image);
        SetStyle(ControlStyles.UserPaint | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        AccessibleName = "高级设置";
        AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Hand;
        TabStop = true;
        _tip.SetToolTip(this, AccessibleName);
        _motion.Tick += Animate;
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!_held && !UnderPointer()) SetTucked(true);
        };
        BuildFace();
    }

    /// <summary>将齿轮建为分层子窗口，由系统合成半透明像素，并按父工作区边界裁切收起的部分。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int layered = 0x00080000;
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= layered;
            return parameters;
        }
    }

    /// <summary>在句柄首次建立及重建后上传当前齿轮，鼠标拖动只移动窗口，无需重新采样。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_face != null) BronzeCornerEdges.CornerWindow.Upload(Handle, _face);
    }

    /// <summary>按当前 DPI 重采样齿轮，将实体、齿尖及镂空边缘的透明度一同提交给窗口合成器。</summary>
    private void BuildFace()
    {
        int length = LogicalToDeviceUnits(54);
        Size = new Size(length, length);
        Bitmap face = new(length, length, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(face))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_source, ClientRectangle, 0, 0, _source.Width, _source.Height, GraphicsUnit.Pixel);
        }
        _face?.Dispose();
        _face = face;
        if (IsHandleCreated) BronzeCornerEdges.CornerWindow.Upload(Handle, face);
    }

    /// <summary>计算吸附终点，收起时将三分之二的按钮移出父控件的裁切区域。</summary>
    internal void Reposition(bool animate = false)
    {
        if (Parent == null || !Visible || _held) return;
        int w = Math.Max(0, Parent.ClientSize.Width - Width), h = Math.Max(0, Parent.ClientSize.Height - Height);
        int x = (int)Math.Round(_fraction * w), y = (int)Math.Round(_fraction * h);
        int inset = _tucked ? Width * 2 / 3 : 0;
        _target = _edge switch
        {
            DockStyle.Left => new(-inset, y), DockStyle.Right => new(w + inset, y),
            DockStyle.Top => new(x, -inset), _ => new(x, h + inset),
        };
        if (animate) _motion.Start();
        else { _motion.Stop(); Location = _target; }
    }

    /// <summary>逐步趋近目标坐标，到位即停表，静止期间不会持续重画远程桌面。</summary>
    private void Animate(object? sender, EventArgs e)
    {
        Location = new Point(Approach(Left, _target.X), Approach(Top, _target.Y));
        if (Location == _target) _motion.Stop();
    }

    /// <summary>每帧至少前进一个像素，使收起和弹回动画能准确停在目标位置。</summary>
    private static int Approach(int value, int target) =>
        value + Math.Sign(target - value) * (int)Math.Ceiling(Math.Abs(target - value) * .38);

    /// <summary>只在收起状态切换时启动动画。</summary>
    private void SetTucked(bool tucked)
    {
        if (_tucked == tucked) return;
        _tucked = tucked;
        Reposition(true);
    }

    /// <summary>以实际轮廓判断悬停，让鼠标留在齿轮上时能持续操作。</summary>
    private bool UnderPointer() => HitFace(PointToClient(MousePosition));

    /// <summary>与原生分层窗口使用同一透明轮廓，孔洞可穿透，轴心可点击。</summary>
    private bool HitFace(Point point) =>
        _face != null && (uint)point.X < _face.Width && (uint)point.Y < _face.Height && _face.GetPixel(point.X, point.Y).A > 0;

    /// <summary>重新开始五秒空闲倒计时。</summary>
    private void ArmIdle() { _idle.Stop(); if (Visible && !_held) _idle.Start(); }

    /// <summary>重新显示入口时先完整展开，离开预览时停止两只计时器。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _motion.Stop();
        _idle.Stop();
        if (!Visible) { _held = _dragging = false; return; }
        _tucked = false;
        Reposition();
        ArmIdle();
    }

    /// <summary>跨显示器时同步像素轮廓、尺寸和吸附位置。</summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BuildFace();
        Reposition();
    }

    /// <summary>分层表面已包含背景透明度，常规画刷无需擦除底色。</summary>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>光标进入露出的一小段时，重新显示完整按钮。</summary>
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _idle.Stop(); SetTucked(false); }

    /// <summary>光标离开后重新计算空闲时长。</summary>
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ArmIdle(); }

    /// <summary>记录按下时指针相对图片的位置，捕获后可连续拖过原生远程画面。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _idle.Stop();
        _motion.Stop();
        _held = true;
        _dragging = false;
        _press = PointToScreen(e.Location);
        _offset = e.Location;
        Capture = true;
    }

    /// <summary>越过系统拖动阈值后跟随指针，在工作区内自由移动。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_held || Parent == null) return;
        Point screen = PointToScreen(e.Location);
        Size threshold = SystemInformation.DragSize;
        _dragging |= Math.Abs(screen.X - _press.X) >= threshold.Width / 2 ||
            Math.Abs(screen.Y - _press.Y) >= threshold.Height / 2;
        if (!_dragging) return;
        Point local = Parent.PointToClient(screen);
        Location = new(Math.Clamp(local.X - _offset.X, 0, Math.Max(0, Parent.ClientSize.Width - Width)),
            Math.Clamp(local.Y - _offset.Y, 0, Math.Max(0, Parent.ClientSize.Height - Height)));
    }

    /// <summary>单击打开侧栏；拖动释放后按距离吸附到最近边缘。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_held || e.Button != MouseButtons.Left) return;
        bool click = !_dragging && HitFace(e.Location);
        if (_dragging) ChooseEdge();
        _held = _dragging = false;
        Capture = false;
        _tucked = false;
        Reposition(true);
        ArmIdle();
        if (click) OnClick(EventArgs.Empty);
    }

    /// <summary>系统取消鼠标捕获时按当前位置归边，结束被打断的拖动。</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || !_held) return;
        if (_dragging) ChooseEdge();
        _held = _dragging = false;
        Reposition(true);
        ArmIdle();
    }

    /// <summary>用四边的空隙决定最近边，并记录沿该边的相对位置。</summary>
    private void ChooseEdge()
    {
        if (Parent == null) return;
        Size area = Parent.ClientSize;
        (int Gap, DockStyle Edge)[] gaps = [(Left, DockStyle.Left), (area.Width - Right, DockStyle.Right),
            (Top, DockStyle.Top), (area.Height - Bottom, DockStyle.Bottom)];
        _edge = gaps.MinBy(item => item.Gap).Edge;
        _fraction = _edge is DockStyle.Left or DockStyle.Right
            ? Top / (double)Math.Max(1, area.Height - Height) : Left / (double)Math.Max(1, area.Width - Width);
        _fraction = Math.Clamp(_fraction, 0, 1);
    }

    /// <summary>键盘焦点进入时展开齿轮，便于用户识别当前选中的操作。</summary>
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SetTucked(false); }

    /// <summary>以回车和空格操作同一个设置入口。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Enter or Keys.Space)) return;
        e.SuppressKeyPress = true;
        OnClick(EventArgs.Empty);
    }

    /// <summary>释放定时器、工具提示和缩放前后的图片。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _idle.Dispose(); _motion.Dispose(); _tip.Dispose(); _source.Dispose(); _face?.Dispose(); }
        base.Dispose(disposing);
    }
}
