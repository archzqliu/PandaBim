using System;
using System.Collections.Generic;

using Rhino;
using Rhino.UI;

using Eto.Forms;
using Eto.Drawing;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using EtoFont = Eto.Drawing.Font;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Rhino.Display;
using SDColor = System.Drawing.Color;

// ===================== 状态（允许重复Run复用窗口） =====================
// ===================== 状态（允许重复Run复用窗口） =====================
static class PlanGenState
{
  public static PlanGenerationPanel Panel = null;


  // ★ Wall2D bridge: PlanGen 落图后触发 Wall2D 刷新+重绘（仅新轴线）
  public static Action<RhinoDoc, int, List<Guid>> Wall2D_PostPlot_RefreshThenRedraw = null;

  public static bool HasLastLocation = false;
  public static Eto.Drawing.Point LastLocation;

  // ===== 记住面板关闭时的数据（行数 + 每行内容 + 尺寸）=====
  public static bool HasLastPanelState = false;
  public static List<PlanGenRowState> LastRows = new List<PlanGenRowState>();

  public static bool HasLastClientSize = false;
  public static Eto.Drawing.Size LastClientSize;

  // 保存±0绝对标高和按钮背景颜色
  public static bool HasEditedBaseAbs = false;
  public static double BaseAbsValue;  // 保存±0绝对标高
  public static double abslevel;  // ✅ 米单位，存 5 位小数（后续统一调用这个）

  public static Eto.Drawing.Color BtnGenBackColor; // 保存按钮背景颜色（黄色）

  // 防止“换了文档还套用上一个文档的内存状态”
  public static uint LastDocSerial = 0;

  // 写进 3dm（下次打开/下次Run 也能恢复）
  const string DocStateKey = "PandaBim.PlanGen.PanelState.v1";

  static string B64(string s)
  {
    if (s == null) s = "";
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
  }
  static string UnB64(string b64)
  {
    if (string.IsNullOrWhiteSpace(b64)) return "";
    try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
    catch { return ""; }
  }

  public static void SaveToDoc(RhinoDoc doc)
  {
    if (doc == null) return;
    try
    {
      // header: v1|w|h|x|y
      int w = HasLastClientSize ? LastClientSize.Width : -1;
      int h = HasLastClientSize ? LastClientSize.Height : -1;
      int x = HasLastLocation ? LastLocation.X : int.MinValue;
      int y = HasLastLocation ? LastLocation.Y : int.MinValue;

      var sb = new StringBuilder();
      string baseAbsStr = HasEditedBaseAbs
      ? abslevel.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture) // ✅ 5位
      : "";
      sb.Append("v1|").Append(w).Append("|").Append(h).Append("|").Append(x).Append("|").Append(y)
        .Append("|").Append(baseAbsStr).Append("|").Append(HasEditedBaseAbs ? "1" : "0").AppendLine();

      if (LastRows != null)
      {
        foreach (var r in LastRows)
        {
          // name|elev|cut|abs  全部 base64
          sb.Append(B64(r.Name)).Append("|")
            .Append(B64(r.Elev)).Append("|")
            .Append(B64(r.Cut)).Append("|")
            .Append(B64(r.Abs)).Append("|")
            .Append(r.LightOn ? "1" : "0").Append("|")
            .Append(r.GenEdited ? "1" : "0").Append("|")
            .Append(r.ModePick.ToString()).Append("|")
            .Append(r.ModeEdited ? "1" : "0").Append("|")
            .Append(B64(r.ParentLayerId == Guid.Empty ? "" : r.ParentLayerId.ToString())).Append("|")
            .Append(B64(r.ParentLayerFullPath ?? "")).Append("|")
            .Append(B64(r.ModelIdsPacked ?? ""))
            .AppendLine();
        }
      }

      doc.Strings.SetString(DocStateKey, sb.ToString());
    }
    catch { }
  }

  public static bool TryLoadFromDoc(
    RhinoDoc doc,
    out List<PlanGenRowState> rows,
    out bool hasClientSize,
    out Eto.Drawing.Size clientSize,
    out bool hasLocation,
    out Eto.Drawing.Point location)
  {
    rows = null;
    hasClientSize = false;
    clientSize = new Eto.Drawing.Size();
    hasLocation = false;
    location = new Eto.Drawing.Point();

    if (doc == null) return false;

    string s = null;
    try { s = doc.Strings.GetValue(DocStateKey); } catch { }
    if (string.IsNullOrWhiteSpace(s)) return false;

    try
    {
      var lines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
      if (lines.Length == 0) return false;

      // header
      var head = lines[0].Split('|');
      if (head.Length >= 5 && head[0] == "v1")
      {
        int w = int.Parse(head[1]);
        int h = int.Parse(head[2]);
        int x = int.Parse(head[3]);
        int y = int.Parse(head[4]);

        // ✅ v1 兼容扩展：支持在 header 中保存±0绝对标高（baseAbs|hasBaseAbs）
        HasEditedBaseAbs = false;
        BaseAbsValue = 0.0;
        if (head.Length >= 7)
        {
          bool hasBaseAbs = (head[6] == "1");
          double baseAbs;
          if (hasBaseAbs && double.TryParse(head[5], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out baseAbs))
          {
            HasEditedBaseAbs = true;
            BaseAbsValue = baseAbs;
            abslevel = baseAbs; // ✅ 同步恢复
          }
        }

        if (w > 0 && h > 0) { hasClientSize = true; clientSize = new Eto.Drawing.Size(w, h); }
        if (x != int.MinValue && y != int.MinValue) { hasLocation = true; location = new Eto.Drawing.Point(x, y); }
      }

      var list = new List<PlanGenRowState>();
      for (int i = 1; i < lines.Length; i++)
      {
        var ln = lines[i];
        if (string.IsNullOrWhiteSpace(ln)) continue;

        var parts = ln.Split('|');
        if (parts.Length < 4) continue;

        var r = new PlanGenRowState();
        r.Name = UnB64(parts[0]);
        r.Elev = UnB64(parts[1]);
        r.Cut  = UnB64(parts[2]);
        r.Abs  = UnB64(parts[3]);
        if (parts.Length >= 6)
        {
          r.LightOn   = (parts[4] == "1");
          r.GenEdited = (parts[5] == "1");
        }
        else if (parts.Length >= 5)
        {       
          // ✅ 兼容旧格式："...|abs|10" 这种把两个flag粘一起的情况
          string flags = parts[4] ?? "";
          if (flags.Length >= 1) r.LightOn   = (flags[0] == '1');
          if (flags.Length >= 2) r.GenEdited = (flags[1] == '1');
        }

        // ✅ 1.3.3：模式/父级图层（图层模式）
        // 扩展格式：name|elev|cut|abs|LightOn|GenEdited|ModePick|ModeEdited|ParentLayerId(b64)|ParentLayerFullPath(b64)|ModelIdsPacked(b64)
        int mp;
        if (parts.Length >= 7 && int.TryParse(parts[6], out mp)) r.ModePick = mp;
        if (parts.Length >= 8) r.ModeEdited = (parts[7] == "1");

        if (parts.Length >= 9)
        {
          string gidStr = UnB64(parts[8]);
          Guid gid;
          if (!string.IsNullOrWhiteSpace(gidStr) && Guid.TryParse(gidStr, out gid)) r.ParentLayerId = gid;
        }
        if (parts.Length >= 10) r.ParentLayerFullPath = UnB64(parts[9]);

        if (parts.Length >= 11) r.ModelIdsPacked = UnB64(parts[10]);
        list.Add(r);
      }

      rows = list;
      return true;
    }
    catch
    {
      return false;
    }
  }
}

// ===== 每行数据的“快照” =====
class PlanGenRowState
{
  public string Name;
  public string Elev;
  public string Cut;
  public string Abs;
  public bool LightOn; // ✅ 新增：记住灯泡状态
  public bool GenEdited;

  // ✅ 1.3.3：模式按钮（图层模式）持久化
  public int ModePick; // 0=Cancel/None, 1=Layer, 2=Model
  public bool ModeEdited;
  public Guid ParentLayerId;
  public string ParentLayerFullPath;


  // ✅ 1.3.4：模型模式：绑定到行的模型对象 Id 列表（Guid;Guid;...），用于持久化
  public string ModelIdsPacked;
}


// ===================== 行结构 =====================
class PlanLevelRow
{
  public Panel Root;

  public Control TxtName;
  public Control TxtElev;
  public Control TxtCut;
  public Control TxtAbs;   // 绝对标高：只读显示用（Panel+Label）

  public TextBox AbsBox;   // 只读显示用，字体/基线跟输入框一致


  public Button BtnGen;
  public Button BtnEdit;
  public Button BtnLight;   // 灯泡按钮（暂不挂功能）
  public Button BtnMode;


  public bool IsBaseRow = false; // 第一行：±0 固定行
  public bool LightOn = false;
  public bool HasModeBtn = true;

  public bool IsSelected  = false;  // ✅ 新增：是否被单击选中（蓝色覆盖）
  public bool HasBulb = true;       // ✅ 是否显示灯泡（±0 行 false）
  public bool HasPlotBtn = true;    // ✅ 是否显示落图按钮（±0 行 false）
  public bool GenEdited = false; // ✅ 本行“标高面”按钮是否已经编辑过一次

  // ✅ 1.3.3：模式按钮（图层模式）绑定到行
  public int ModePick = 0;
  public bool ModeEdited = false;
  public Guid ParentLayerId = Guid.Empty;
  public string ParentLayerFullPath = "";


  

  // ✅ 1.3.4：模型模式：绑定到行的模型对象 Id 列表（不含曲线）
  public List<Guid> ModelIds = new List<Guid>();
}


// ===================== 主面板 =====================

class PlanGenerationPanel : Form
{
  // —— 颜色 / 字体（按你 Wall2D 的风格习惯）——
  readonly Color _dark       = Color.FromArgb(56, 56, 56);
  readonly Color _lightBox   = Color.FromArgb(180, 180, 180);
  readonly Color _titleColor = Color.FromArgb(230, 230, 230);
  readonly Color _buttonBack = Color.FromArgb(70, 70, 70);
  readonly Color _buttonText = Color.FromArgb(230, 230, 230);
  readonly Color _rowBack    = Color.FromArgb(46, 46, 46); // 行背景：更深灰
  readonly Color _gridLine   = Color.FromArgb(65, 65, 65); // 分格线
  readonly Color _cellTextColor = Color.FromArgb(220, 220, 220); // ✅ 输入框文字稍暗（可调 180~210）
  readonly Color _btnBorder = Color.FromArgb(55, 55, 55); // ✅ 按钮框线更暗（可再调 45~65）


  readonly EtoFont _headerFont = new EtoFont("Arial", 10f);
  readonly EtoFont _cellFont   = new EtoFont("Arial", 10f);
  readonly EtoFont _btnFont    = new EtoFont("Arial", 9f);
  readonly EtoFont _topBtnFont = new EtoFont("Arial", TopBtnFontSize);
  

  // ===== 1.3.7：落图防重线用的临时缓存结构 =====
  class PlotNewCurve
  {
    public Guid GeoId = Guid.Empty;      // 来源几何体真实 Id
    public Guid BlockId = Guid.Empty;    // ✅ 1.5.5：所属块实例 Id（非块=Guid.Empty）
    public string BlockPath = "";       // ✅ 1.5.6：嵌套块路径（不含 BlockId1；用 "/" 分隔各层 definition 内 InstanceObject Id）
    public string SrcName = "";          // 来源几何体 Name（用于继承参数）
    public string AxisType = "";         // 预计算的曲线类型
    public Curve Crv = null;             // 已挪到 Z=0 的曲线副本
    public string SigKey = "";           // 2D 签名（Start/End/Mid，忽略 Z）
  }


  // ===== 1.5.3：求交临时结果（曲线 + 来源 Name + GeoId，支持 Block 叶子对象）=====
  class PlotHitCurve
  {
    public Guid GeoId = Guid.Empty;      // 叶子来源 Id（1.5.3 阶段1：Block 直接用 definition 内对象 Id；后续可升级为派生 Id）
    public Guid BlockId = Guid.Empty;    // ✅ 1.5.5：所属块实例 Id（非块=Guid.Empty）
    public string BlockPath = "";       // ✅ 1.5.6：嵌套块路径（不含 BlockId1；用 "/" 分隔各层 definition 内 InstanceObject Id）
    public string SrcName = "";          // 叶子来源 Name（用于继承参数）
    public Curve Crv = null;             // 求交得到的曲线（3D，后续会 Duplicate + 挪到 Z=0）
  }

  class PlotOldCurve
  {
    public Guid CurveId = Guid.Empty;    // 旧曲线自身 Id
    public Guid GeoId = Guid.Empty;      // Name 中记录的 GeoId（可能为空）
    public Guid BlockId = Guid.Empty;    // ✅ 1.5.5：Name 中记录的 BlockId（可能为空）
    public string BlockPath = "";       // ✅ 1.5.6：Name 中记录的 BlockId2..n（"/" 分隔；可能为空）
    public string SigKey = "";           // 2D 签名
  }

    // ✅ 1.5.6：用 (BlockPath + GeoId) 作为落图同步的唯一键（解决 Block 复制/嵌套后 GeoId 撞车导致反复刷新）
  // BlockId = BlockId1（最外层块实例 Id；非块=Guid.Empty）
  // BlockPath = BlockId2..n（"/" 分隔；可能为空；写入 Name 时会展开为 BlockId2=...|BlockId3=...）
  struct PlotGeoBlockKey : IEquatable<PlotGeoBlockKey>
  {
    public Guid GeoId;
    public Guid BlockId;
    public string BlockPath;

    public PlotGeoBlockKey(Guid geoId, Guid blockId, string blockPath)
    {
      GeoId = geoId;
      BlockId = blockId;
      BlockPath = (blockPath ?? "");
    }

    public bool Equals(PlotGeoBlockKey other)
    {
      return GeoId == other.GeoId
          && BlockId == other.BlockId
          && string.Equals(BlockPath ?? "", other.BlockPath ?? "", StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
      if (obj is PlotGeoBlockKey) return Equals((PlotGeoBlockKey)obj);
      return false;
    }

    public override int GetHashCode()
    {
      unchecked
      {
        int h = 17;
        h = h * 31 + GeoId.GetHashCode();
        h = h * 31 + BlockId.GetHashCode();
        h = h * 31 + ((BlockPath ?? "").GetHashCode());
        return h;
      }
    }
  }

// ✅ 选中行的蓝色覆盖（你要更像 Rhino 可微调）
  readonly Color _rowSelBack  = Color.FromArgb(72, 137, 255);
  readonly Color _rowSelText  = Color.FromArgb(245, 245, 245);
  // 按钮变成黄色
  readonly Color _baseAbsOkBack = Color.FromArgb(255, 180, 0); // 黄色
  readonly Color _baseAbsOkText = Color.FromArgb(0, 0, 0);     // 黑色
  // 不限模式按钮：红色（不推荐）
  readonly Color _modeAllOkBack = Color.FromArgb(220, 60, 60); // 红色
  readonly Color _modeAllOkText = Color.FromArgb(255, 255, 255); // 白字

  // ✅ native → row 映射：用于 WPF/WinForms 反射拦截单击/双击
  readonly Dictionary<object, PlanLevelRow> _nativeCellRow = new Dictionary<object, PlanLevelRow>();
  readonly Dictionary<object, Func<bool>>   _nativeCanEdit = new Dictionary<object, Func<bool>>();
  readonly Dictionary<object, TextBox>      _nativeEtoBox  = new Dictionary<object, TextBox>();
  
  // ✅ 行选中（单击）状态
  PlanLevelRow _selectedRow = null;

  // ✅ ↕ 排序状态：第一次点击=升序；再点=降序
  bool _sortElevAscending = true;

  // ===== 双击进入编辑：运行态 =====
  TextBox _editingCell = null;

  // native(TextBox) -> Eto TextBox
  readonly Dictionary<object, TextBox> _nativeCellMap = new Dictionary<object, TextBox>();

  // ✅ 行右键菜单：保持引用，避免菜单闪退
  ContextMenu _activeRowContextMenu = null;

  // ✅ Eto TextBox -> 所属行（用于 native 吞事件时也能选中整行）
  readonly Dictionary<TextBox, PlanLevelRow> _tbRowMap = new Dictionary<TextBox, PlanLevelRow>();


  // Eto TextBox -> 是否允许编辑（用于±0行锁死）
  readonly Dictionary<TextBox, Func<bool>> _cellCanEdit = new Dictionary<TextBox, Func<bool>>();
  // ✅ 记录“进入编辑前”的文本：用于 EndEditCell 判断是否发生改名
  readonly Dictionary<TextBox, string> _editBeginText = new Dictionary<TextBox, string>();



  // —— 布局参数：默认显示11行（标题不滚动）——
  const int VisibleRowCount = 10;

  const int ColNameW = 70; //命令行宽度
  const int ColElevW = 70;
  const int ColCutW  = 70;
  const int ColAbsW  = 70;
  const int BtnW     = 50;

  // ===== 顶层按钮专用参数（独立调节大小与字号）=====
  const int TopBtnW = 44;          // 顶层按钮宽度
  const int TopBtnH2 = 24;         // 顶层按钮高度
  const float TopBtnFontSize = 9f; // 顶层按钮字号

  const int RowH     = 24;
  const int RowGap   = 4;

  const int TopBtnH  = 24;
  const int HeaderH  = 24;

  const int BulbW = 28;   // 灯泡按钮列宽（可微调 26~32）

  //Button _btnZero;
  Button _btnAdd;
  Button _btnMinus;
  Button _btnSwap; // ↕
  Button _btnCO;   // ✅ 1.4.4：复制标高行


  StackLayout _rowsStack;
  Scrollable  _scroll;

  readonly List<PlanLevelRow> _rows = new List<PlanLevelRow>();
  int _rowSerial = 0;

  // ===================== ClippingPlane <-> 标高行：实时双向同步（1.2.1） =====================
  bool _docEventsHooked = false;
  bool _isClosingPanel  = false;

  // ✅ 1.3.3：图层模式提示窗（必须是非模态，允许用户去点选当前图层）
  Form _layerModePickForm = null;

  class ClipXformWatchItem
  {
    public Guid Id;
    public string Name;

    // ✅ 记录变换前截平面的 Plane.Origin（用于非法移动时回滚）
    public Point3d OldOrigin;
    public bool HasOldOrigin;
  }

  // TransformEventId -> 本次变换涉及的截平面（只缓存 clip layer 上的 clippingplane）
  readonly Dictionary<uint, List<ClipXformWatchItem>> _xformWatch = new Dictionary<uint, List<ClipXformWatchItem>>();

  // 防回环：当“面板驱动移动剖切面”时，屏蔽一次 AfterTransform/Replace 对该 Name 的回写
  readonly HashSet<string> _suppressClipNameSync = new HashSet<string>(StringComparer.Ordinal);


  // ✅ 6.7：标高截平面拖动时，若程序随动移动了对应的 -clip，为避免 Replace 事件重复随动，记录最近一次随动时间（UTC Ticks）
  readonly Dictionary<string, long> _lastFollowMoveUtcTicksByRowName = new Dictionary<string, long>(StringComparer.Ordinal);

  // 节流更新：拖动时最多 20Hz 刷新标高行
  readonly Dictionary<string, double> _pendingAbs5ByRowName = new Dictionary<string, double>(StringComparer.Ordinal);
  readonly Dictionary<string, double> _lastAbs5ByRowName    = new Dictionary<string, double>(StringComparer.Ordinal);

  // ✅ 剖切高度同步：拖动 cut clippingplane（-clip）时，按 20Hz 回写 TxtCut
  readonly Dictionary<string, double> _pendingCutMeterByRowName = new Dictionary<string, double>(StringComparer.Ordinal);
  readonly Dictionary<string, double> _lastCutMeterByRowName    = new Dictionary<string, double>(StringComparer.Ordinal);

  
  // ✅ 删除收敛：记录本轮被删除的 clippingplane 名称（延迟一帧判定，避免 Replace/移动造成误判）
  readonly HashSet<string> _pendingDeletedClipNames = new HashSet<string>(StringComparer.Ordinal);
UITimer _clipSyncTimer = null;
  bool   _clipSyncTimerRunning = false; // 1.2.1: UI节流定时器运行态

  string _activeClipRowName = null;

  Bitmap _bulbOnImg;
  Bitmap _bulbOffImg;

  public PlanGenerationPanel()
  {
    Title = "PandaBim-PlanGen";
    try { RhinoApp.WriteLine("[PlanGen] Build tag: 1.4.4_CO_copyRow"); } catch { }
    Resizable = false;

    BackgroundColor = _dark;

    _bulbOnImg  = BuildBulbIcon(true);
    _bulbOffImg = BuildBulbIcon(false);

    BuildUi();

    // ✅ 先恢复“上次关闭时的行数 + 数据”
    RestoreRowsAndWindowState();

    
    // ✅ 打开面板即解锁 ClippingPlan 图层
    UnlockClippingPlanLayerForPanel();
    ShowAllClippingPlanesOnClipLayer();   // ✅ 新增：启动面板显示全部 clippingplane
    RefreshAllAbsCellsFromClip();

    InitClipSyncTimer();

    // ✅ 兜底：按钮的扁平化样式在 LoadComplete 时会按“默认灰底”再刷一遍；
    //   因此在面板真正显示后，再统一刷新一遍每行视觉（含±0按钮黄/灰），保证“重启面板”外观完全一致。
    Shown += (s, e) =>
    {
      try
      {
        RhinoApp.WriteLine("[PlanGen] Panel shown. Content=" + (this.Content==null?"null":this.Content.GetType().Name) + " Client=" + this.ClientSize.Width + "x" + this.ClientSize.Height + " Rows=" + _rows.Count);
        HookDocEvents();
        foreach (var r in _rows)
        {
          if (r != null) ApplyRowSelectionVisual(r);
        }
      }
      catch { }
    };


    // 关闭时：记位置 + 记尺寸 + 记每一行数据（并写入3dm）
    Closed += (s, e) =>
    {
      _isClosingPanel = true;

      // ✅ 1.3.3：关闭面板时，确保关闭“图层模式”提示窗
      try
      {
        if (_layerModePickForm != null)
        {
          try { _layerModePickForm.Close(); } catch { }
          try { _layerModePickForm.Dispose(); } catch { }
          _layerModePickForm = null;
        }
      }
      catch { }
      UnhookDocEvents();
      // ✅ 关闭面板即锁定 ClippingPlan 图层
      ForceAllBulbsOff(); 
      SyncActiveViewportClippingByBulbs();   // ✅ 全灭 → 当前视图取消所有本图层剖切
      HideAllClippingPlanesOnClipLayer(); // ✅ 新增：关闭面板隐藏全部 clippingplane
      LockClippingPlanLayerForPanel();
      SaveRowsAndWindowState();
      PlanGenState.Panel = null;
    };

    // 设Owner，避免“弹了但被挡/焦点不对”
    try { this.Owner = RhinoEtoApp.MainWindow; } catch { }
    try { this.ShowInTaskbar = false; } catch { }
  }

  void BuildUi()
  {
    //_btnZero  = MakeTopButton("±0");
    _btnAdd   = MakeTopButton("➕");
    _btnMinus = MakeTopButton("➖");
    _btnSwap  = MakeTopButton("▼");   // ✅ 新增：先不赋功能
    _btnCO    = MakeTopButton("CO");  // ✅ 1.4.4：复制标高行

    // 仅实现 + ：新增行
    _btnAdd.Click += (s, e) =>
    {
        var r = AddRowInternal();
        ApplyDefaultNameForNewRow(r);
        SelectRow(r);
        ForceAllBulbsOff();              // ✅ 新建标高层后：默认关闭所有小灯泡
        SyncActiveViewportClippingByBulbs(); // ✅ 同步：取消当前视图剖切
    };

    _btnMinus.Click += (s, e) => DeleteSelectedRow();
    _btnSwap.Click += (s, e) => SortRowsByElevToggle();

    // ✅ 1.4.4：复制当前激活（蓝色）的标高行
    _btnCO.Click += (s, e) => CopySelectedRow();


    var topButtons = new TableLayout
    {
      Padding = new Padding(12, 10, 12, 0),
      Spacing = new Size(8, 0),
      Rows =
      {
        new TableRow(_btnAdd, _btnMinus,  _btnSwap, _btnCO, null)
      }
    };

    var headerRow = new TableLayout
    {
      Padding = new Padding(12, 0, 12, 0),
      Spacing = new Size(0, 0), // ✅ 关键：不要间距，用分格线
      Rows =
      {
        new TableRow(
          OuterVLine(HeaderH),
          MakeHeader("名称", ColNameW), VLine(HeaderH),
          MakeHeader("标高", ColElevW), VLine(HeaderH),
          MakeHeader("剖切高度", ColCutW), VLine(HeaderH),
          MakeHeader("绝对标高", ColAbsW), VLine(HeaderH),
          new Panel { Width = BulbW, Height = HeaderH, BackgroundColor = _dark }, VLine(HeaderH), // ✅ 灯泡列（表头空）
          MakeHeader("标高面", BtnW), VLine(HeaderH),
          MakeHeader("模式", BtnW),  VLine(HeaderH), 
          MakeHeader("图纸", BtnW),
          OuterVLine(HeaderH)
        )
      }
    };

    _rowsStack = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 0,
      Padding = new Padding(0, 6, 0, 0),
      HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    int scrollH = VisibleRowCount * RowH + (VisibleRowCount - 1) * RowGap + 8;

    _scroll = new Scrollable
    {
      Border = BorderType.None,
      BackgroundColor = _dark,
      Padding = new Padding(12, 0, 12, 0),
      Content = _rowsStack,
      Height = scrollH
    };

    var topButtonsWrap = new Panel { BackgroundColor = _dark, Content = topButtons };
    var headerRowWrap  = new Panel { BackgroundColor = _dark, Content = headerRow };

    var mainStack = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 0,
      Items =
      {
        topButtonsWrap,
        new Panel { BackgroundColor = _dark, Padding = new Padding(12, 6, 12, 0), Content = OuterHLine() },

        headerRowWrap,
        new Panel
        {
          BackgroundColor = _dark,
          Padding = new Padding(12, 0, 12, 0),
          Content = OuterHLine()
        },
        _scroll
      }
    };

    // ✅ root 再包一层 Panel，强制背景色（某些环境下 StackLayout/Scrollable 透明会“露出白底”）
    Content = new Panel { BackgroundColor = _dark, Content = mainStack };

    int w = 12 + TableGridWidth() + 12 + 32;
    int h = 10 + TopBtnH + 8 + HeaderH + scrollH + 20;
    ClientSize = new Size(w, h);

  }

  Button MakeTopButton(string text)
  {
    var btn = new Button
    {
      Text = text,
      Width = TopBtnW,
      Height = TopBtnH2,
      Font = _topBtnFont,
      BackgroundColor = _buttonBack,
      TextColor = _buttonText
    };
    btn.LoadComplete += (s, e) => TryMakeButtonFlat(btn, btn.BackgroundColor, _btnBorder);
    TryMakeButtonFlat(btn, _buttonBack, _btnBorder);

    return btn;
  }

  Label MakeHeader(string text, int width)
  {
    return new Label
    {
      Text = text,
      Width = width,
      Height = HeaderH,
      TextColor = _titleColor,
      Font = _headerFont,
      VerticalAlignment = VerticalAlignment.Center,
      TextAlignment = TextAlignment.Center
    };
  }

  TextBox MakeCellBox(int width)
  {
    var tb = new TextBox
    {
      Width = width,
      Height = RowH,
      Font = _cellFont,

      BackgroundColor = _rowBack,
      TextColor = _titleColor,

      TextAlignment = TextAlignment.Center,
      Text = "x.xxx"
    };

    tb.LoadComplete += (s, e) => TryMakeTextBoxFlat(tb, _rowBack);
    tb.GotFocus     += (s, e) => TryMakeTextBoxFlat(tb, _rowBack);
    tb.LostFocus    += (s, e) => TryMakeTextBoxFlat(tb, _rowBack);

    TryMakeTextBoxFlat(tb, _rowBack);
    return tb;
  }

  TextBox MakeAbsBox(int width)
  {
    var tb = new TextBox
    {
      Width = width,
      Height = RowH,
      Font = _cellFont,
      BackgroundColor = _rowBack,
      TextColor = _cellTextColor,
      TextAlignment = TextAlignment.Center,
      Text = "x.xxx",
      ReadOnly = true
    };

    tb.LoadComplete += (s, e) => TryMakeTextBoxFlat(tb, _rowBack);
    tb.GotFocus     += (s, e) => { try { this.Focus(); } catch { } };
    TryMakeTextBoxFlat(tb, _rowBack);

    return tb;
  }

  int TableGridWidth()
  {
    // 7列：名称/标高/剖切高度/绝对标高/灯泡/编辑/落图
    // 内部分隔线 6 条 + 外侧 2 条
    return ColNameW + ColElevW + ColCutW + ColAbsW + BulbW + BtnW + BtnW + BtnW + 7 + 2;
  }

  Panel VLine(int h)       => new Panel { Width = 1, Height = h, BackgroundColor = _gridLine };
  Panel OuterVLine(int h)  => new Panel { Width = 1, Height = h, BackgroundColor = _gridLine };
  Panel OuterHLine()       => new Panel { Width = TableGridWidth(), Height = 1, BackgroundColor = _gridLine };

  Button MakeRowButton(string text)
  {
    var btn = new Button
    {
      Text = text,
      Width = BtnW,
      Height = RowH,
      Font = _btnFont,
      BackgroundColor = _buttonBack,
      TextColor = _buttonText
    };
    btn.LoadComplete += (s, e) => TryMakeButtonFlat(btn, btn.BackgroundColor, _btnBorder);
    TryMakeButtonFlat(btn, _buttonBack, _btnBorder);

    return btn;
  }

  Button MakeBulbButton()
  {
    var btn = new Button
    {
      Width = BulbW,
      Height = RowH,
      Font = _btnFont,
      BackgroundColor = _rowBack,
      TextColor = _buttonText,
      Text = "",
      Image = _bulbOffImg // ✅ 默认灭（因为新增行默认 LightOn=false）
    };
    btn.LoadComplete += (s, e) => TryMakeButtonFlat(btn, btn.BackgroundColor, _btnBorder);
    TryMakeButtonFlat(btn, _rowBack, _btnBorder);

    return btn;
  }

  Bitmap BuildBulbIcon(bool on)
  {
    int w = 16, h = 16;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgba);

    using (var g = new Graphics(bmp))
    {
      g.Clear(Color.FromArgb(0, 0, 0, 0)); // 透明背景 (R,G,B,A)

      // 你当前的设定我不动（你自己在调色）
      var stroke = new Pen(Color.FromArgb(250, 250, 250), 0.5f);

      Color glassFill = on
        ? Color.FromArgb(255, 220, 14)   // 黄
        : Color.FromArgb(111, 161, 230); // 蓝灰

      Color baseFill = on
        ? Color.FromArgb(213, 163, 176)
        : Color.FromArgb(111, 161, 230);

      // 你当前的尺寸我不动
      var glassRect = new RectangleF(4.175f, 3.175f, 7.65f, 7.65f);
      g.FillEllipse(new SolidBrush(glassFill), glassRect);
      g.DrawEllipse(stroke, glassRect);

      var baseRect = new RectangleF(6.3f, 12.3f, 3.4f, 3.4f);
      g.FillRectangle(new SolidBrush(baseFill), baseRect);
      // g.DrawRectangle(stroke, baseRect);
    }

    return bmp;
  }

  void ApplyBulbVisual(PlanLevelRow row)
  {
    if (row == null || row.BtnLight == null) return;
    if (!row.HasBulb) { row.BtnLight.Image = null; row.BtnLight.Text = " "; return; } // ✅ 兜底
    row.BtnLight.Image = row.LightOn ? _bulbOnImg : _bulbOffImg;
    row.BtnLight.Text = "";
  }

  // ✅ 互斥：只能亮一个，或全灭
  void ToggleExclusiveBulb(PlanLevelRow clicked)
  {
    if (clicked == null) return;

    // 点已亮的：允许全灭
    if (clicked.LightOn)
    {
      clicked.LightOn = false;
      ApplyBulbVisual(clicked);
      return;
    }

    // 否则：点亮它，并熄灭其他
    foreach (var r in _rows)
    {
      if (r == null) continue;
      bool shouldOn = (r == clicked);

      if (r.LightOn != shouldOn)
      {
        r.LightOn = shouldOn;
        ApplyBulbVisual(r);
      }
    }
  }

  // ✅ 5.3：关闭面板时强制熄灭所有小灯泡
  void ForceAllBulbsOff()
 {
  foreach (var r in _rows)
  {
    if (r == null) continue;
    if (!r.HasBulb) continue;   // ±0 行没有灯泡
    if (!r.LightOn) continue;

    r.LightOn = false;
    ApplyBulbVisual(r);         // 顺手刷一下图标（可留可不留）
  }
 }



  // ✅ 恢复后兜底：如果历史数据里出现“多个亮”，只保留第一个亮；若本来全灭，则保持全灭
  void NormalizeExclusiveBulbs()
  {
    PlanLevelRow firstOn = null;
    foreach (var r in _rows)
    {
      if (r != null && r.LightOn) { firstOn = r; break; }
    }

    foreach (var r in _rows)
    {
      if (r == null) continue;
      bool shouldOn = (firstOn != null && r == firstOn);
      if (r.LightOn != shouldOn) r.LightOn = shouldOn;
      ApplyBulbVisual(r);
    }
  }

  // ➕按钮用：新增“普通可编辑行”
  public void AddRow()
  {
    var r = AddRowInternal();
    ApplyDefaultNameForNewRow(r);
    // ✅ 新建标高层后：默认关闭所有小灯泡（全灭）
    ForceAllBulbsOff();
    SyncActiveViewportClippingByBulbs();
  }

  // 真正建行
  PlanLevelRow AddRowInternal()
  {
    _rowSerial++;

    var row = new PlanLevelRow();
    row.TxtName = MakeCellBox(ColNameW);
    row.TxtElev = MakeCellBox(ColElevW);
    row.TxtCut  = MakeCellBox(ColCutW);
    // ✅ 单击没反应，双击才可编辑（±0行后续会把 IsBaseRow=true，所以这里用函数实时判断）
    RegisterCellTextBox(row.TxtName as TextBox, () => !row.IsBaseRow);
    RegisterCellTextBox(row.TxtElev as TextBox, () => !row.IsBaseRow);
    RegisterCellTextBox(row.TxtCut  as TextBox, () => !row.IsBaseRow);


    var absBox = MakeAbsBox(ColAbsW);
    row.TxtAbs = absBox;
    row.AbsBox = absBox;

    // ✅ 绑定：让 native handler 能通过 tb 找到 row
    BindCellToRow(row.TxtName as TextBox, row);
    BindCellToRow(row.TxtElev as TextBox, row);
    BindCellToRow(row.TxtCut  as TextBox, row);
    BindCellToRow(absBox, row);
    // ✅ Abs 也必须注册：否则双击可能被 BeginEditCell 解锁
    RegisterCellTextBox(absBox, () => false);

    // ✅ 这里挂：单击任何输入栏都选中整行
    HookSelectRow(row.TxtName, row);
    HookSelectRow(row.TxtElev, row);
    HookSelectRow(row.TxtCut,  row);
    HookSelectRow(row.TxtAbs,  row);

    // ✅ 新增行默认灭（保证“只能亮一个/或全灭”的初始一致性）
    row.LightOn = false;

    // 1) 先创建三个按钮（一定要先 new 出来）
    row.BtnLight = MakeBulbButton();
    row.BtnGen  = MakeRowButton("➕");
    row.BtnMode  = MakeRowButton("选择");
    row.BtnEdit = MakeRowButton("落图");
    // 2) 再挂事件
    row.BtnLight.Click += (s, e) =>
    {
        SelectRow(row);
        if (!row.HasBulb) return;     // ✅ ±0 行：空格子，不干活
        ToggleExclusiveBulb(row);
        SyncActiveViewportClippingByBulbs();   // ✅ 灯泡亮/灭 → 立即影响当前视图剖切
    };

    row.BtnEdit.Click += (s, e) =>
    {
        SelectRow(row);
        if (!row.HasPlotBtn) return;  // ✅ ±0 行：空格子，不干活

        // ✅ 只有“非±0行”才需要被拦截
        if (!row.IsBaseRow && !EnsureBaseAbsIsNumeric())
            return;

        // 8.1：未选择模式 => 提示
        if (!IsRowModeReadyForPlot(row))
        {
          try
          {
            Eto.Forms.MessageBox.Show(
              this,
              "请先选择模式；",
              "提示",
              Eto.Forms.MessageBoxButtons.OK,
              Eto.Forms.MessageBoxType.Information);
          }
          catch
          {
            try { Rhino.UI.Dialogs.ShowMessageBox("请先选择模式；", "提示"); } catch { }
          }
          return;
        }

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;

        // 8.3：优先 -clip，其次 base；两者都没有 => 报错
        RhinoObject clipRo;
        Plane clipPlane;
        if (!TryGetRowPlotPlane(doc, row, out clipRo, out clipPlane))
        {
          try
          {
            Eto.Forms.MessageBox.Show(
              this,
              "请先添加标高面后再进行落图操作；",
              "提示",
              Eto.Forms.MessageBoxButtons.OK,
              Eto.Forms.MessageBoxType.Warning);
          }
          catch
          {
            try { Rhino.UI.Dialogs.ShowMessageBox("请先添加标高面后再进行落图操作；", "提示"); } catch { }
          }
          return;
        }

        // 8.2：拿到本行绑定的模型对象集合（图层模式/模型模式/不限图层）
        var targets = CollectRowPlotTargets(doc, row, clipRo != null ? clipRo.Id : Guid.Empty);
        if (targets == null || targets.Count == 0)
        {
          RhinoApp.WriteLine("[PlanGen] 落图：未找到可求交的模型对象。");
          return;
        }

        // 8.6：确保输出图层 PandaBim-Plan::<RowName>::PB-Wall-Axis
        string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
        int outLi = EnsurePlanOutputAxisLayer(doc, rowName);
        if (outLi < 0 || outLi >= doc.Layers.Count)
        {
          RhinoApp.WriteLine("[PlanGen] 落图：输出图层创建失败。");
          return;
        }

        double tol = doc.ModelAbsoluteTolerance;
        if (tol <= 0.0) tol = RhinoMath.SqrtEpsilon;

        // 8.5：垂直移动到 Z=0（世界坐标系）
        double dz = -clipPlane.OriginZ;
        var xformToZero = Rhino.Geometry.Transform.Translation(0.0, 0.0, dz);

        // 8.7：本轮求交先生成“新曲线”（先不落图），并在 Name 最后写入 GeoId
        var newCurves = new List<PlotNewCurve>();

        foreach (var ro in targets)
        {
          if (ro == null) continue;
          if (ro.Id == Guid.Empty) continue;
          if (clipRo != null && ro.Id == clipRo.Id) continue; // 安全：别把截平面自己拿去求交

          if (!IsObjectVisibleForPlot(doc, ro)) continue;

          // ✅ 排除线/点/截平面本身
          if (ro.ObjectType == ObjectType.Curve) continue;
          if (ro.ObjectType == ObjectType.Point) continue;
          if (ro.ObjectType == ObjectType.ClipPlane) continue;

          var hits = IntersectOneObjectWithPlaneEx(doc, ro, clipPlane, tol);
          if (hits == null || hits.Count == 0) continue;

          foreach (var hit in hits)
          {
            if (hit == null) continue;

            var crv0 = hit.Crv;
            if (crv0 == null) continue;
            if (!crv0.IsValid) continue;
            if (crv0.IsShort(tol)) continue;

            Guid geoId = hit.GeoId;
            Guid blockId = Guid.Empty;
            try { blockId = hit.BlockId; } catch { blockId = Guid.Empty; }
            string blockPath = "";
            try { blockPath = hit.BlockPath ?? ""; } catch { blockPath = ""; }
            string srcName = "";
            try { srcName = (hit.SrcName ?? "").Trim(); } catch { srcName = ""; }

            Curve crv = null;
            try { crv = crv0.DuplicateCurve(); } catch { crv = null; }
            if (crv == null || !crv.IsValid) continue;

            // 垂直挪到 Z=0
            try
            {
              if (!RhinoMath.EpsilonEquals(dz, 0.0, 1e-12))
                crv.Transform(xformToZero);
            }
            catch { }

            string axisType = GetAxisTypeFromCurve(crv, tol);
            string sigKey = GetCurveSig2DKey(crv);
            if (string.IsNullOrWhiteSpace(sigKey)) continue;

            newCurves.Add(new PlotNewCurve
            {
              GeoId = geoId,
              BlockId = blockId,
              BlockPath = blockPath,
              SrcName = srcName,
              AxisType = axisType,
              Crv = crv,
              SigKey = sigKey
            });
          }
        }

        // 8.7.1：扫描旧曲线（本行 PB-Wall-Axis 图层）
        var oldCurves = new List<PlotOldCurve>();
        try
        {
          if (outLi >= 0 && outLi < doc.Layers.Count)
          {
            var ly = doc.Layers[outLi];
            var objs = doc.Objects.FindByLayer(ly); // ✅ 数组快照
            if (objs != null)
            {
              foreach (var oo in objs)
              {
                if (oo == null) continue;
                if (oo.ObjectType != ObjectType.Curve) continue;

                var oc = oo.Geometry as Curve;
                if (oc == null || !oc.IsValid) continue;

                Guid gid = Guid.Empty;
                Guid bid = Guid.Empty;
                string nm = "";
                try { nm = (oo.Attributes?.Name ?? "").Trim(); } catch { nm = ""; }
                TryParseGeoIdFromName(nm, out gid); // 解析失败则为 Guid.Empty
                string bpath = "";
                TryParseBlockPathFromName(nm, out bid, out bpath); // 解析失败则为 Guid.Empty


                // ✅ 1.3.7：旧曲线若无 GeoId（大小写不敏感），视为其它模块生成：不参与同步，也不删除
                if (gid == Guid.Empty) continue;
                string sig = "";
                try { sig = GetCurveSig2DKey(oc); } catch { sig = ""; }
                if (string.IsNullOrWhiteSpace(sig)) sig = "";

                oldCurves.Add(new PlotOldCurve
                {
                  CurveId = oo.Id,
                  GeoId = gid,
                  BlockId = bid,
                  BlockPath = bpath,
                  SigKey = sig
                });
              }
            }
          }
        }
        catch { }

        // 8.7.2：按 (BlockId + GeoId) 分组
        var oldByKey = new Dictionary<PlotGeoBlockKey, List<PlotOldCurve>>();
        foreach (var o in oldCurves)
        {
          if (o == null) continue;
          if (o.GeoId == Guid.Empty) continue;

          var key = new PlotGeoBlockKey(o.GeoId, o.BlockId, o.BlockPath);
          if (!oldByKey.TryGetValue(key, out var list) || list == null)
          {
            list = new List<PlotOldCurve>();
            oldByKey[key] = list;
          }
          list.Add(o);
        }

        var newByKey = new Dictionary<PlotGeoBlockKey, List<PlotNewCurve>>();
        foreach (var n in newCurves)
        {
          if (n == null) continue;
          if (n.GeoId == Guid.Empty) continue;

          var key = new PlotGeoBlockKey(n.GeoId, n.BlockId, n.BlockPath);
          if (!newByKey.TryGetValue(key, out var list) || list == null)
          {
            list = new List<PlotNewCurve>();
            newByKey[key] = list;
          }
          list.Add(n);
        }

        var oldToDelete = new HashSet<Guid>();
        var newToSkip = new HashSet<PlotNewCurve>(); // class → 引用判等（足够）

        // 8.7.4：情况 1：旧有、新无 → 删除旧（以 BlockId+GeoId 为键）
        foreach (var kv in oldByKey)
        {
          var key = kv.Key;
          if (key.GeoId == Guid.Empty) continue;

          if (!newByKey.ContainsKey(key))
          {
            var list = kv.Value;
            if (list == null) continue;
            foreach (var o in list)
              if (o != null && o.CurveId != Guid.Empty)
                oldToDelete.Add(o.CurveId);
          }
        }

        // 8.7.5：情况 2/3：旧新同 (BlockId+GeoId) → 对比签名（并顺便清理旧曲线内部重线 + 新曲线内部重线）
        foreach (var kv in oldByKey)
        {
          var key = kv.Key;
          if (key.GeoId == Guid.Empty) continue;
          if (!newByKey.TryGetValue(key, out var nlist) || nlist == null) continue;

          var olist = kv.Value;
          if (olist == null) continue;

          // 新签名集合
          var newSigSet = new HashSet<string>(StringComparer.Ordinal);
          foreach (var n in nlist)
            if (n != null && !string.IsNullOrWhiteSpace(n.SigKey))
              newSigSet.Add(n.SigKey);

          // 情况 2：旧签名不在新集合中 → 删除旧
          foreach (var o in olist)
          {
            if (o == null) continue;
            if (o.CurveId == Guid.Empty) continue;
            if (string.IsNullOrWhiteSpace(o.SigKey)) { oldToDelete.Add(o.CurveId); continue; }

            if (!newSigSet.Contains(o.SigKey))
              oldToDelete.Add(o.CurveId);
          }

          // 旧曲线内部去重（同 BlockId+GeoId 下同签名只保留一根）
          var seenOld = new HashSet<string>(StringComparer.Ordinal);
          foreach (var o in olist)
          {
            if (o == null) continue;
            if (o.CurveId == Guid.Empty) continue;
            if (oldToDelete.Contains(o.CurveId)) continue;

            string sig = o.SigKey ?? "";
            if (string.IsNullOrWhiteSpace(sig)) { oldToDelete.Add(o.CurveId); continue; }

            if (seenOld.Contains(sig))
              oldToDelete.Add(o.CurveId);
            else
              seenOld.Add(sig);
          }

          // 剩余旧签名集合（删掉的旧不参与“完全一致”判定）
          var oldSigSet = new HashSet<string>(StringComparer.Ordinal);
          foreach (var o in olist)
          {
            if (o == null) continue;
            if (o.CurveId == Guid.Empty) continue;
            if (oldToDelete.Contains(o.CurveId)) continue;

            if (!string.IsNullOrWhiteSpace(o.SigKey))
              oldSigSet.Add(o.SigKey);
          }

          // 新曲线内部去重 + 情况 3：若新签名已在旧集合中 → 不落图（跳过）
          var seenNew = new HashSet<string>(StringComparer.Ordinal);
          foreach (var n in nlist)
          {
            if (n == null) continue;
            if (string.IsNullOrWhiteSpace(n.SigKey)) { newToSkip.Add(n); continue; }

            if (seenNew.Contains(n.SigKey)) { newToSkip.Add(n); continue; }
            seenNew.Add(n.SigKey);

            if (oldSigSet.Contains(n.SigKey))
              newToSkip.Add(n);
          }
        }

        // 新 key 组里也做一次“内部去重”（避免求交本身返回重复段）
        foreach (var kv in newByKey)
        {
          var key = kv.Key;
          if (key.GeoId == Guid.Empty) continue;
          if (oldByKey.ContainsKey(key)) continue;

          var nlist = kv.Value;
          if (nlist == null) continue;

          var seenNew = new HashSet<string>(StringComparer.Ordinal);
          foreach (var n in nlist)
          {
            if (n == null) continue;
            if (string.IsNullOrWhiteSpace(n.SigKey)) { newToSkip.Add(n); continue; }

            if (seenNew.Contains(n.SigKey))
              newToSkip.Add(n);
            else
              seenNew.Add(n.SigKey);
          }
        }

        // 8.7.6：先删旧
        int deletedCount = 0;
        if (oldToDelete.Count > 0)
        {
          deletedCount = oldToDelete.Count;
          foreach (var id in oldToDelete)
          {
            if (id == Guid.Empty) continue;
            try { doc.Objects.Delete(id, true); } catch { }
          }
        }

        // 8.7.7：再落图“剩余的新曲线”
        int createdCount = 0;
        var createdAxisIds = new List<Guid>();

        foreach (var n in newCurves)
        {
          if (n == null) continue;
          if (n.GeoId == Guid.Empty) continue;
          if (n.Crv == null || !n.Crv.IsValid) continue;

          if (newToSkip.Contains(n)) continue;

          var attr = new ObjectAttributes();
          attr.LayerIndex = outLi;
          attr.ColorSource = ObjectColorSource.ColorFromLayer;
          attr.Name = ""; // 先空着

          Guid cid = Guid.Empty;
          try { cid = doc.Objects.AddCurve(n.Crv, attr); } catch { cid = Guid.Empty; }
          if (cid == Guid.Empty) continue;
          try { createdAxisIds.Add(cid); } catch { }

          createdCount++;

          // 8.4：Name 继承与改写 + 追加 GeoId（最后一段）
          string axisType = n.AxisType;
          if (string.IsNullOrWhiteSpace(axisType))
            axisType = GetAxisTypeFromCurve(n.Crv, tol);

          string newName = "";
          if (!string.IsNullOrWhiteSpace(n.SrcName))
            newName = BuildAxisNameFromSource(n.SrcName, cid, axisType, n.Crv, n.GeoId, n.BlockId, n.BlockPath);
          else
            newName = BuildAxisNameBasic(cid, axisType, n.Crv, n.GeoId, n.BlockId, n.BlockPath);

          TryApplyCurveAttrs(doc, cid, outLi, newName);
        }

                // ★ PlanGen -> Wall2D：先刷新（清理已删轴线对应的墙线），再重绘（仅本轮新轴线）
        if (deletedCount > 0 || createdCount > 0)
        {
          try
          {
            var act = PlanGenState.Wall2D_PostPlot_RefreshThenRedraw;
            if (act != null) act(doc, outLi, createdAxisIds);
          }
          catch { }
        }

// ✅ 仅删旧不生成新线时也要强制刷新视图，否则会出现“残影”
        if (deletedCount > 0 || createdCount > 0)
        {
          try { doc.Views.Redraw(); } catch { }
        }

        if (createdCount > 0)
        {
          if (deletedCount > 0)
            RhinoApp.WriteLine("[PlanGen] 落图完成：生成相交线 {0} 条，删除旧线 {1} 条。", createdCount, deletedCount);
          else
            RhinoApp.WriteLine("[PlanGen] 落图完成：生成相交线 {0} 条。", createdCount);
        }
        else
        {
          if (deletedCount > 0)
            RhinoApp.WriteLine("[PlanGen] 落图完成：删除旧线 {0} 条；本轮没有生成新的相交线。", deletedCount);
          else
            RhinoApp.WriteLine("[PlanGen] 落图：没有生成任何相交线。");
        }
    };

    row.BtnMode.Click += (s, e) =>
    {
        SelectRow(row);
        if (!row.HasModeBtn) return;     // ✅ ±0 行：空格子，不干活

        // ✅（可选但建议一致）：±0 未设定绝对标高时，禁止其它行按钮操作
        if (!row.IsBaseRow && !EnsureBaseAbsIsNumeric())
            return;

        
        // 7.x：模式按钮
        var pick = ShowModePickDialog(row);
        if (pick == ModePickResult.Layer)
        {
          // ✅ 1.3.3：图层模式：允许用户在提示期间去点选“当前图层”，再确认
          ShowLayerModePickPrompt(row);
        }
        else if (pick == ModePickResult.Model)
        {
          // ✅ 1.3.4：模型模式：让用户选择需要落图的模型（不含曲线），记录 Guid 并绑定到当前行
          RunPickModelsForRow(row);
        }

    };

    row.BtnGen.Click += (s, e) =>
    {
        SelectRow(row);

        // ✅ ±0 行：编辑按钮用于点取绝对标高
        if (row.IsBaseRow)
        {
            PickBaseAbsFromPoint(row);
            return;
        }

        // ✅ 只有“非±0行”才需要被拦截
        if (!row.IsBaseRow && !EnsureBaseAbsIsNumeric())
            return;

        // ✅ Esc/取消时要回滚灯泡状态：先做快照
        var bulbSnapshot = new List<bool>(_rows.Count);
        foreach (var rr in _rows) bulbSnapshot.Add(rr != null && rr.LightOn);

        // ✅ 进入编辑/新增：立刻全灭所有灯泡
        ForceAllBulbsOff();

        // ✅ 同步：全灭 => 当前视图取消本图层所有 clipping 剖切效果
        SyncActiveViewportClippingByBulbs();

        bool ok = RunPickClippingPlaneForRow(row); // ✅ 仅成功创建/编辑才返回 true

        if (!ok)
        {
            // ✅ 取消：不改按钮状态（不变黄/不变“编辑”），并回滚灯泡状态
            for (int i = 0; i < _rows.Count && i < bulbSnapshot.Count; i++)
            {
                var rr = _rows[i];
                if (rr == null) continue;
                if (!rr.HasBulb) continue; // ±0 行跳过
                rr.LightOn = bulbSnapshot[i];
                ApplyBulbVisual(rr);
            }
            RefreshAbsCellFromClip(row);
            SyncActiveViewportClippingByBulbs();
            ApplyRowSelectionVisual(row);
            return;
        }
        RefreshAbsCellFromClip(row);   // ✅ 兜底：确保能从doc里读回并刷新显示


        // ✅ 成功：点亮当前行灯泡（互斥，只亮一个）
        if (row.HasBulb && !row.LightOn) ToggleExclusiveBulb(row);

        // ✅ 同步：点亮当前 => 激活当前截平面对本视图剖切
        SyncActiveViewportClippingByBulbs();

        row.GenEdited = true;

        ApplyRowSelectionVisual(row);
        SaveRowsAndWindowState();
    };
    // 3) 最后再刷新灯泡外观
    ApplyBulbVisual(row);



    var rowTable = new TableLayout
    {
      Spacing = new Size(0, 0),
      Rows =
      {
        new TableRow(
          OuterVLine(RowH),
          row.TxtName, VLine(RowH),
          row.TxtElev, VLine(RowH),
          row.TxtCut,  VLine(RowH),
          row.TxtAbs,  VLine(RowH),
          row.BtnLight, VLine(RowH),
          row.BtnGen,  VLine(RowH),
          row.BtnMode, VLine(RowH),
          row.BtnEdit,
          OuterVLine(RowH)
        )
      }
    };

    row.Root = new Panel
    {
      BackgroundColor = _rowBack,
      Content = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        Items = { rowTable, OuterHLine() }
      }
    };

    // ✅ 右键菜单：点整行空白处也能触发（更像图层面板的体验）
    HookSelectRow(row.Root, row);

    _rows.Add(row);
    _rowsStack.Items.Add(row.Root);

    return row;
  }

  void SelectRow(PlanLevelRow row)
 {
  if (row == null) return;

  // ✅ 如果正在编辑某个单元格：点击到“其它行”时，自动结束编辑
  if (_editingCell != null)
  {
    PlanLevelRow editingRow = null;
    if (!_tbRowMap.TryGetValue(_editingCell, out editingRow))
      editingRow = null;

    // 只有当点击的是“不同的行”才退出编辑（点击同一行不打断）
    if (editingRow == null || editingRow != row)
      EndEditCell(_editingCell);
  }

  if (_selectedRow == row) return;

  // 取消旧选中
  if (_selectedRow != null)
  {
    _selectedRow.IsSelected = false;
    ApplyRowSelectionVisual(_selectedRow);
  }

  _selectedRow = row;
  _selectedRow.IsSelected = true;
  ApplyRowSelectionVisual(_selectedRow);
 }

// ===================== 7 模式按钮：模式选择对话框（仅 UI，不接入业务） =====================
enum ModePickResult
{
  Cancel = 0,
  Layer = 1,
  Model = 2,
  AllDoc = 3 // ✅ 不限图层：未来落图选择整个文件的所有模型（不含曲线）
}

ModePickResult ShowModePickDialog(PlanLevelRow row)
{
  // 只负责弹窗并返回选择；业务逻辑后续接入
  try
  {
    var dlg = new Dialog<ModePickResult>();
    dlg.Title = "选择模式";
    dlg.Resizable = false;
    dlg.Padding = new Padding(12);
    dlg.ClientSize = new Size(760, 160);

    // 黑底
    var back = new Color(0.12f, 0.12f, 0.12f);
    dlg.BackgroundColor = back;

    string tip =
      "“图层模式”识别选中图层下的所有子图层模型（可识别后续新建模型，推荐此模式）" +
      System.Environment.NewLine +
      "“模型模式”仅识别本次选中的模型（仅识别选中模型的改动（ID不变），且无法识别后续新建模型）";

    var lbl = new Label
    {
      Text = tip,
      TextAlignment = TextAlignment.Left,
      Wrap = WrapMode.None,
      TextColor = Colors.White,
      BackgroundColor = back
    };

    var btnLayer = new Button { Text = "图层模式", Width = 120 };
    var btnModel = new Button { Text = "模型模式", Width = 120 };

    // 按钮：灰底黑字
    var btnBack = new Color(0.78f, 0.78f, 0.78f);
    var btnBorder = new Color(0.60f, 0.60f, 0.60f);

    try { btnLayer.BackgroundColor = btnBack; } catch { }
    try { btnModel.BackgroundColor = btnBack; } catch { }

    try { btnLayer.TextColor = Colors.Black; } catch { }
    try { btnModel.TextColor = Colors.Black; } catch { }

    // 尽量用 Rhino 现有的扁平化按钮工具，避免受系统主题影响
    try { TryMakeButtonFlat(btnLayer, btnBack, btnBorder); } catch { }
    try { TryMakeButtonFlat(btnModel, btnBack, btnBorder); } catch { }

    btnLayer.Click += (s, e) =>
    {
      try { dlg.Close(ModePickResult.Layer); } catch { }
    };

    btnModel.Click += (s, e) =>
    {
      try { dlg.Close(ModePickResult.Model); } catch { }
    };

    // 按钮组
    var btnStack = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 12,
      Items = { btnLayer, btnModel }
    };

    // 居中：左右伸缩 spacer + 中间按钮组
    var spacerL = new Panel();
    var spacerR = new Panel();

    var btnCenterRow = new TableLayout
    {
      Spacing = new Size(0, 0),
      Padding = new Padding(0),
      Rows =
      {
        new TableRow(
          new TableCell(spacerL, true),
          new TableCell(btnStack, false),
          new TableCell(spacerR, true)
        )
      }
    };

    dlg.Content = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 10,
      Items = { lbl, btnCenterRow }
    };

    // Owner：优先挂到当前面板；失败则退回 Rhino 主窗口
    try { return dlg.ShowModal(this); }
    catch
    {
      try { return dlg.ShowModal(RhinoEtoApp.MainWindow); }
      catch { return ModePickResult.Cancel; }
    }
  }
  catch
  {
    return ModePickResult.Cancel;
  }
}

// ✅ 1.3.3：图层模式提示（非模态，允许用户去图层面板激活当前图层）
void ShowLayerModePickPrompt(PlanLevelRow row)
{
  if (row == null) return;

  try
  {
    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    // 关闭上一次弹窗（避免多开）
    try
    {
      if (_layerModePickForm != null)
      {
        try { _layerModePickForm.Close(); } catch { }
        try { _layerModePickForm.Dispose(); } catch { }
      }
    }
    catch { }
    _layerModePickForm = null;

    var frm = new Form();
    _layerModePickForm = frm;

    frm.Title = "图层模式";
    frm.Resizable = false;
    frm.Padding = new Padding(12);
    frm.ClientSize = new Size(760, 160);

    // 黑底
    var back = new Color(0.12f, 0.12f, 0.12f);
    frm.BackgroundColor = back;

    var lbl = new Label
    {
      Text = "请激活某图层后点击确定，此图层下所有子图层模型都将被持续识别。",
      TextAlignment = TextAlignment.Left,
      Wrap = WrapMode.Word,
      TextColor = Colors.White,
      BackgroundColor = back
    };

    var btnAll = new Button { Text = "不限图层（不推荐）", Width = 180 };

    var btnOk = new Button { Text = "确定", Width = 120 };
    var btnCancel = new Button { Text = "取消", Width = 120 };

    // 按钮：灰底黑字
    var btnBack = new Color(0.78f, 0.78f, 0.78f);
    var btnBorder = new Color(0.45f, 0.45f, 0.45f);


    // ✅ 不限模式按钮：红色（不推荐）
    btnAll.BackgroundColor = _modeAllOkBack;
    btnAll.TextColor = _modeAllOkText;
    TryMakeButtonFlat(btnAll, _modeAllOkBack, _modeAllOkBack);
    btnOk.BackgroundColor = btnBack;
    btnOk.TextColor = Colors.Black;
    TryMakeButtonFlat(btnOk, btnBack, btnBorder);

    btnCancel.BackgroundColor = btnBack;
    btnCancel.TextColor = Colors.Black;
    TryMakeButtonFlat(btnCancel, btnBack, btnBorder);

    btnAll.Click += (s, e) =>
    {
      try
      {
        // ✅ 不限图层：未来落图将枚举整个 3dm 内的所有模型对象（不含曲线），且不受后续新建图层影响
        row.ModePick = (int)ModePickResult.AllDoc;
        row.ModeEdited = true;
        row.ParentLayerId = Guid.Empty;
        row.ParentLayerFullPath = "ALL";

        try { row.ModelIds.Clear(); } catch { }
        ApplyRowSelectionVisual(row);
        SaveRowsAndWindowState();

        try { this.Focus(); } catch { }
        try { frm.Close(); } catch { }
      }
      catch { }
    };

    btnCancel.Click += (s, e) =>
    {
      try { frm.Close(); } catch { }
    };

    btnOk.Click += (s, e) =>
    {
      try
      {
        var d = RhinoDoc.ActiveDoc;
        if (d == null) return;

        var lay = d.Layers.CurrentLayer;
        if (lay == null)
        {
          try { Rhino.UI.Dialogs.ShowMessageBox("未能读取当前图层，请先在图层面板激活一个图层。", "图层模式"); } catch { }
          return;
        }

        // ✅ 写入到“当前行”
        row.ModePick = (int)ModePickResult.Layer;
        row.ModeEdited = true;
        row.ParentLayerId = lay.Id;
        row.ParentLayerFullPath = lay.FullPath ?? (lay.Name ?? "");


        try { row.ModelIds.Clear(); } catch { }
        // ✅ 立即刷新按钮外观并持久化
        ApplyRowSelectionVisual(row);
        SaveRowsAndWindowState();

        try { this.Focus(); } catch { }

        try { frm.Close(); } catch { }
      }
      catch { }
    };

    frm.Closed += (s, e) =>
    {
      try
      {
        if (ReferenceEquals(_layerModePickForm, frm))
        {
          _layerModePickForm = null;
        }
      }
      catch { }
    };

    // 居中按钮
    var spacerL = new Panel();
    var spacerR = new Panel();

    var btnCenterRow = new TableLayout
    {
      Spacing = new Size(12, 0),
      Padding = new Padding(0),
      Rows =
      {
        new TableRow(
          new TableCell(spacerL, true),
          btnAll,
          btnOk,
          btnCancel,
          new TableCell(spacerR, true)
        )
      }
    };

    frm.Content = new TableLayout
    {
      Spacing = new Size(0, 12),
      Padding = new Padding(0),
      Rows =
      {
        lbl,
        btnCenterRow
      }
    };

    try { frm.Owner = RhinoEtoApp.MainWindow; } catch { }

    // ✅ 非模态显示：期间允许用户去点选当前图层
    frm.Show();
  }
  catch { }
}





  void ApplyRowSelectionVisual(PlanLevelRow row)
 {
  if (row == null) return;

  bool sel = row.IsSelected;

  Color back = sel ? _rowSelBack : _rowBack;
  Color text = sel ? _rowSelText : _cellTextColor;

  void ApplyTb(Control c)
  {
    var tb = c as TextBox;
    if (tb == null) return;
    tb.BackgroundColor = back;
    tb.TextColor = text;
    TryMakeTextBoxFlat(tb, back);
  }

  ApplyTb(row.TxtName);
  ApplyTb(row.TxtElev);
  ApplyTb(row.TxtCut);
  ApplyTb(row.TxtAbs);

  // 灯泡列：正常行=按钮；±0 行=空单元格占位
  // 灯泡格
  // === 灯泡格：普通行显示灯泡；±0 行显示空格子 ===
  if (row.BtnLight != null)
  {
    if (!row.HasBulb)
    {
        // ✅ ±0 行：跟整行同色（选中蓝/未选中深灰）
        MakeButtonAsEmptyCell(row.BtnLight, back);
    }
    else
    {
        // ✅ 普通行：背景也要跟整行同色，这样选中时才能盖住灯泡格
        row.BtnLight.BackgroundColor = back;
        row.BtnLight.TextColor       = text;

         ApplyBulbVisual(row);

        // 选中时边框同色更“融”；未选中用按钮边框
        Color bulbBorder = sel ? back : _btnBorder;
        TryMakeButtonFlat(row.BtnLight, back, bulbBorder);
    }
  }
  // 编辑按钮：选中变蓝；取消选中恢复按钮浅灰
    // 编辑按钮：±0 且绝对标高有效 -> 永远黄底黑字；否则按选中/未选中
  // 编辑按钮：±0 且绝对标高有效 -> 永远黄底黑字；否则按选中/未选中
  if (row.BtnGen != null)
  {
    bool baseAbsOk = false;
    if (row.IsBaseRow)
    {
      double v;
      baseAbsOk = TryParseElevNumber(GetCtrlText(row.TxtAbs), out v);
    }

    bool genOk = row.IsBaseRow ? baseAbsOk : row.GenEdited;

    // ✅ 文案：编辑过一次就显示“编辑”
    row.BtnGen.Text = genOk ? "编辑" : "➕";

    Color genBack = genOk
      ? _baseAbsOkBack
      : (sel ? _rowSelBack : _buttonBack);

    Color genText = genOk
      ? _baseAbsOkText
      : (sel ? _rowSelText : _buttonText);

    row.BtnGen.BackgroundColor = genBack;
    Color genBorder = sel ? genBack : _btnBorder;
    TryMakeButtonFlat(row.BtnGen, genBack, genBorder);

    row.BtnGen.TextColor       = genText;
  }


   // === 模式按钮：普通行=按钮；±0 行=空格子 ===
  if (row.BtnMode != null)
  {
    if (!row.HasModeBtn)
    {
        MakeButtonAsEmptyCell(row.BtnMode, back);
    }
    else
    {
        // ✅ 1.3.4：模式按钮外观：图层=黄色，“不限”=红色，“模型”=蓝色
        bool modeOkModel = row.ModeEdited
          && row.ModePick == (int)ModePickResult.Model
          && row.ModelIds != null
          && row.ModelIds.Count > 0;

        bool modeOkLayer = row.ModeEdited
          && row.ModePick == (int)ModePickResult.Layer
          && row.ParentLayerId != Guid.Empty;

        bool modeOkAll = row.ModeEdited
          && row.ModePick == (int)ModePickResult.AllDoc;

        if (modeOkAll) row.BtnMode.Text = "不限";
        else if (modeOkModel) row.BtnMode.Text = "模型";
        else if (modeOkLayer) row.BtnMode.Text = "图层";
        else row.BtnMode.Text = "选择";

        Color bBackM;
        Color bTextM;

        if (modeOkAll)
        {
          bBackM = _modeAllOkBack;
          bTextM = _modeAllOkText;
        }
        else if (modeOkLayer)
        {
          bBackM = _baseAbsOkBack;
          bTextM = _baseAbsOkText;
        }
        else if (modeOkModel)
        {
          // ✅ 模型模式：按钮保持“行选中蓝”
          bBackM = _rowSelBack;
          bTextM = _rowSelText;
        }
        else
        {
          bBackM = sel ? _rowSelBack : _buttonBack;
          bTextM = sel ? _rowSelText : _buttonText;
        }

        row.BtnMode.BackgroundColor = bBackM;
        row.BtnMode.TextColor       = bTextM;

        Color bBorderM = (sel || modeOkModel) ? bBackM : _btnBorder;
        TryMakeButtonFlat(row.BtnMode, bBackM, bBorderM);
}
  }



  // 落图按钮：正常行=按钮；±0 行=空单元格占位
  // 落图格
  // === 落图格：普通行按钮；±0 行空格子 ===
  if (row.BtnEdit != null)
  {
  if (!row.HasPlotBtn)
   {
    // ✅ ±0 行：跟整行同色
    MakeButtonAsEmptyCell(row.BtnEdit, back);
   }
   else
   {
    Color bBack2 = sel ? _rowSelBack : _buttonBack;
    Color bText2 = sel ? _rowSelText : _buttonText;

    row.BtnEdit.BackgroundColor = bBack2;
    row.BtnEdit.TextColor       = bText2;

    Color bBorder2 = sel ? bBack2 : _btnBorder;
    TryMakeButtonFlat(row.BtnEdit, bBack2, bBorder2);
   }
  }
  // Row 根面板也设一下（即使被控件盖住也无害）
  if (row.Root != null) row.Root.BackgroundColor = back;
 }

  string GetNextLayerName()
 {
  int maxN = 0;

  foreach (var r in _rows)
  {
    if (r == null) continue;

    string name = GetCtrlText(r.TxtName);
    if (string.IsNullOrWhiteSpace(name)) continue;

    name = name.Trim();

    // 只认：层 + 纯数字（层01、层2、层003 都算）
    if (!name.StartsWith("层")) continue;

    string numPart = name.Substring(1);
    if (string.IsNullOrWhiteSpace(numPart)) continue;

    bool allDigits = true;
    for (int i = 0; i < numPart.Length; i++)
    {
      if (numPart[i] < '0' || numPart[i] > '9') { allDigits = false; break; }
    }
    if (!allDigits) continue;

    if (int.TryParse(numPart, out int n))
      if (n > maxN) maxN = n;
   }

  int next = maxN + 1;
  return "层" + next.ToString("D2"); // 01,02... 99,100...
 }

  void ApplyDefaultNameForNewRow(PlanLevelRow row)
 {
  if (row == null) return;

  // 基准行（±0）不需要自动名
  if (row.IsBaseRow) return;

  var tb = row.TxtName as TextBox;
  if (tb == null) return;

  tb.Text = GetNextLayerName();
 }


  // ===================== 1.4.4：复制标高行（CO） =====================
  // 规则：在最下方新增一行，名称用默认“层0n”，其余（标高/剖切高度/绝对标高/标高面/模式）从当前激活行复制；
  //      若源行存在标高截平面，则新行需创建“同样几何”的新截平面（新名字绑定新行），避免共享 Name 引发错绑。
  void CopySelectedRow()
  {
    var src = _selectedRow;
    if (src == null) return;

    // ✅ 固定 ±0 行不复制（避免产生“第二个±0”的歧义）
    if (src.IsBaseRow)
    {
      RhinoApp.WriteLine("[PlanGen] ±0 行不能复制。请选中普通标高行再复制。");
      return;
    }

    // 1) 新建行（自动插到底部）
    var dst = AddRowInternal();
    if (dst == null) return;
    ApplyDefaultNameForNewRow(dst);

    // 2) 复制文本数据（保持用户输入原样，不强制重算）
    try { SetCtrlText(dst.TxtElev, GetCtrlText(src.TxtElev)); } catch { }
    try { SetCtrlText(dst.TxtCut,  GetCtrlText(src.TxtCut)); } catch { }
    try { SetCtrlText(dst.TxtAbs,  GetCtrlText(src.TxtAbs)); } catch { }

    // 3) 复制“模式”绑定数据
    try
    {
      dst.ModePick           = src.ModePick;
      dst.ModeEdited         = src.ModeEdited;
      dst.ParentLayerId      = src.ParentLayerId;
      dst.ParentLayerFullPath= src.ParentLayerFullPath;

      if (dst.ModelIds != null) dst.ModelIds.Clear();
      if (src.ModelIds != null && dst.ModelIds != null)
      {
        foreach (var g in src.ModelIds)
          if (g != Guid.Empty) dst.ModelIds.Add(g);
      }
    }
    catch { }

    // 4) 复制“标高面”（若源行存在同名 clippingplane，则克隆一份到新名）
    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc != null)
      {
        // 面板打开时本来会解锁；这里兜底一次
        UnlockClippingPlanLayerForPanel();

        bool wasLocked;
        int clipLi = EnsureClipTopLayer(doc, out wasLocked);
        if (clipLi >= 0)
        {
          string srcName = (GetCtrlText(src.TxtName) ?? "").Trim();
          string dstName = (GetCtrlText(dst.TxtName) ?? "").Trim();

          if (!string.IsNullOrWhiteSpace(srcName) && !string.IsNullOrWhiteSpace(dstName))
          {
            var srcCp = FindClipPlaneByNameOnClipLayer(doc, clipLi, srcName);
            if (srcCp != null)
            {
              Plane pl;
              double w, h;
              if (TryGetClipPlanePlaneAndSize(doc, srcCp, out pl, out w, out h))
              {
                // 防御：若目标名残留同名截平面，先清掉
                try { DeleteClipPlanesByName(doc, clipLi, dstName); } catch { }
                try { DeleteClipPlanesByName(doc, clipLi, dstName + "-clip"); } catch { }

                var attr = new ObjectAttributes();
                attr.LayerIndex = clipLi;
                attr.Name       = dstName;

                Guid id = AddClippingPlaneSafe(doc, pl, w, h, attr);
                if (id != Guid.Empty)
                {
                  // ✅ 颜色/标签统一（仅对新建对象设置）
                  try { TrySetClipPlaneObjectColor(doc, id, System.Drawing.Color.Blue); } catch { }
                  try { TrySetClippingPlaneLabelAsText(doc, id, dstName); } catch { }

                  dst.GenEdited = true;

                  // 若剖切高度为有效数值，则按新行生成派生 -clip（绑定新行名）
                  double cutMeter;
                  if (TryParseElevNumber(GetCtrlText(dst.TxtCut), out cutMeter))
                  {
                    if (cutMeter >= 0.0 && cutMeter <= 40.0)
                    {
                      try { EnsureCutClippingPlaneForRowByMeter(doc, clipLi, dstName, cutMeter, false); } catch { }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
    catch { }

    // 5) 选中新行（更符合“复制后继续编辑”的直觉）
    SelectRow(dst);
    ApplyRowSelectionVisual(dst);

    // 6) 记录并刷新
    try { SaveRowsAndWindowState(); } catch { }
    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc != null) doc.Views.Redraw();
    }
    catch { }
  }


  void DeleteSelectedRow()
 {
  if (_selectedRow == null) return;

  // ✅ 固定 ±0 行不允许删
  if (_selectedRow.IsBaseRow)
  {
    RhinoApp.WriteLine("[PlanGen] ±0 行不能删除。");
    return;
  }

  // ✅ Rhino8 原生风格确认框：Yes / No
  string name = GetCtrlText(_selectedRow.TxtName);
  if (string.IsNullOrWhiteSpace(name)) name = "未命名";

  string msg =
    $"是否确定删除“{name}”标高层，删除后该层平面图将失去模型绑定，之后重建同名标高层可恢复绑定。";

  var dr = Eto.Forms.MessageBox.Show(
    this,                                  // 父窗口
    msg,
    "删除确认",
    Eto.Forms.MessageBoxButtons.YesNo,
    Eto.Forms.MessageBoxType.Warning);

  if (dr != Eto.Forms.DialogResult.Yes)
    return;

  int idx = _rows.IndexOf(_selectedRow);
  if (idx < 0) return;

  // 先解除选中
  var deleting = _selectedRow;
  _selectedRow = null;

  // ✅ 5.5：删除标高层时，同步删除 ClippingPlan 中“同名”的 clippingplane
  try
  {
   var doc = RhinoDoc.ActiveDoc;
   if (doc != null && deleting != null)
   {
    string rowName = (GetCtrlText(deleting.TxtName) ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(rowName))
    {
      // 取 ClippingPlan 图层
      bool wasLocked;
      int clipLi = EnsureClipTopLayer(doc, out wasLocked); // 你现成的函数
      if (clipLi >= 0)
      {
        // ✅ 6.4：删除本行的标高截平面（rowName）以及派生剖切截平面（rowName-clip）
        DeleteClipPlanesByName(doc, clipLi, rowName);
        DeleteClipPlanesByName(doc, clipLi, rowName + "-clip");
      }
    }
   }
  }
  catch { }

  // 从 UI 移除
  _rows.RemoveAt(idx);
  if (idx >= 0 && idx < _rowsStack.Items.Count)
    _rowsStack.Items.RemoveAt(idx);

  // 灯泡互斥兜底（如果删的是点亮的那行，会自然变成全灭）
  NormalizeExclusiveBulbs();

  // 删除后自动选中相邻行（更像 Rhino）
  if (_rows.Count > 0)
  {
    int newIdx = Math.Min(idx, _rows.Count - 1);
    SelectRow(_rows[newIdx]);
    // ✅ 兜底：删行后强制刷新一次 ±0 行视觉（保证黄底不丢）
    for (int i = 0; i < _rows.Count; i++)
    {
        var r = _rows[i];
        if (r != null && r.IsBaseRow)
        {
            ApplyRowSelectionVisual(r);
            break;
        }
    }
  }
  // ✅ 删除标高层/剖切面后：立即同步剖切并强制重绘，清掉残影
  try
  {
    SyncActiveViewportClippingByBulbs();   // 你现成的：会按灯泡重设当前视图剖切
  }
  catch { }

  try
  {
   var doc = RhinoDoc.ActiveDoc;
   if (doc != null)
   {
    // 用 UI 线程兜底触发 Redraw（避免需要鼠标点一下才刷新）
    RhinoApp.InvokeOnUiThread((Action)(() =>
    {
      try { doc.Views.Redraw(); } catch { }
    }));
   }
  }
  catch { }

 }

 void SortRowsByElevToggle()
 {
  // ✅ 如果正在编辑某个格子：先退出编辑，避免光标残留
  if (_editingCell != null)
    EndEditCell(_editingCell);

  SortRowsByElev(_sortElevAscending);

  // ✅ 下次点击反向
  _sortElevAscending = !_sortElevAscending;
 }

 void SortRowsByElev(bool ascending)
 {
  if (_rows == null || _rows.Count <= 1) return;

  // 1) 找到 ±0 行（不一定永远是 index=0，所以用 IsBaseRow 更稳）
  PlanLevelRow baseRow = null;
  for (int i = 0; i < _rows.Count; i++)
  {
    if (_rows[i] != null && _rows[i].IsBaseRow)
    {
      baseRow = _rows[i];
      break;
    }
  }

  // 2) 收集其它行（需要保留原始顺序，用于“同标高时稳定排序”）
  var items = new List<RowSortItem>();
  for (int i = 0; i < _rows.Count; i++)
  {
    var r = _rows[i];
    if (r == null) continue;
    if (r == baseRow) continue;

    double key;
    bool ok = TryParseElevNumber(GetCtrlText(r.TxtElev), out key);

    // 解析失败：放到最后（升序）或最前（降序）都可以；
    // 这里按“解析失败永远排最后”处理，更符合直觉
    if (!ok) key = double.PositiveInfinity;

    items.Add(new RowSortItem { Row = r, Key = key, OriginalIndex = i });
  }

  // 3) 排序
  items.Sort((a, b) =>
  {
    int cmp = a.Key.CompareTo(b.Key);
    if (cmp == 0) cmp = a.OriginalIndex.CompareTo(b.OriginalIndex); // 稳定
    return ascending ? cmp : -cmp;
  });

  // 4) 生成新顺序：±0 行永远第一
  var newOrder = new List<PlanLevelRow>();
  if (baseRow != null) newOrder.Add(baseRow);
  for (int i = 0; i < items.Count; i++) newOrder.Add(items[i].Row);

  // 5) 重排 _rows
  _rows.Clear();
  _rows.AddRange(newOrder);

  // 6) 重排 UI（_rowsStack 里的行容器）
  _rowsStack.Items.Clear();
  for (int i = 0; i < _rows.Count; i++)
  {
    var r = _rows[i];
    if (r != null && r.Root != null)
      _rowsStack.Items.Add(r.Root);
  }

  // 7) 选中状态/颜色刷新（保持原来选中行）
  for (int i = 0; i < _rows.Count; i++)
  {
    var r = _rows[i];
    if (r == null) continue;
    r.IsSelected = (r == _selectedRow);
    ApplyRowSelectionVisual(r);
  }
 }

  // —— 工具：解析“标高”字符串为数字 ——
  // 支持： 3.200 / -3.500 / +1.000 / "  2.800m " 之类
  bool TryParseElevNumber(string s, out double value)
 {
  value = 0.0;
  if (string.IsNullOrWhiteSpace(s)) return false;

  s = s.Trim();

  // 只保留数字/正负号/小数点/逗号（逗号有时候是千分位或小数点）
  var sb = new StringBuilder();
  for (int i = 0; i < s.Length; i++)
  {
    char ch = s[i];
    if ((ch >= '0' && ch <= '9') || ch == '-' || ch == '+' || ch == '.' || ch == ',')
      sb.Append(ch);
  }

  var cleaned = sb.ToString();
  if (string.IsNullOrWhiteSpace(cleaned)) return false;

  // 先按当前文化解析，再按 InvariantCulture 解析（双保险）
  if (double.TryParse(cleaned, out value)) return true;

  var inv = System.Globalization.CultureInfo.InvariantCulture;
  return double.TryParse(cleaned,
    System.Globalization.NumberStyles.Float,
    inv,
    out value);
 }

  // ✅ 前置校验：±0 行“绝对标高”必须是数字，否则禁止其它行编辑/落图
  bool EnsureBaseAbsIsNumeric()
 {
  if (PlanGenState.HasEditedBaseAbs)
  return true;
  // 找到 ±0 行
  PlanLevelRow baseRow = null;
  for (int i = 0; i < _rows.Count; i++)
  {
    var r = _rows[i];
    if (r != null && r.IsBaseRow) { baseRow = r; break; }
  }

  // 理论上一定存在；不存在就不拦截
  if (baseRow == null) return true;

  string absText = GetCtrlText(baseRow.TxtAbs);
  double v;
  if (TryParseElevNumber(absText, out v))
    return true;

  // 不是数字：提示并拦截
  Eto.Forms.MessageBox.Show(
    this,
    "请先编辑±0，确定其绝对标高。",
    "提示",
    Eto.Forms.MessageBoxButtons.OK,
    Eto.Forms.MessageBoxType.Information);

  // 可选但很顺手：自动选中 ±0 行，提示用户下一步操作
  SelectRow(baseRow);

  return false;
 }


  class RowSortItem
 {
  public PlanLevelRow Row;
  public double Key;
  public int OriginalIndex;
 }



  // ✅ 用于“恢复上次关闭时的数据”（包含灯泡状态）
  void AddRow(PlanGenRowState st)
  {
    var row = AddRowInternal();

    if (st != null)
    {
      SetCtrlText(row.TxtName, st.Name);
      SetCtrlText(row.TxtElev, st.Elev);
      SetCtrlText(row.TxtCut,  st.Cut);
      SetCtrlText(row.TxtAbs,  st.Abs);

      row.LightOn = st.LightOn;
      row.GenEdited = st.GenEdited;

      // ✅ 1.3.3：恢复“图层模式”绑定信息
      row.ModePick = st.ModePick;
      row.ModeEdited = st.ModeEdited;
      row.ParentLayerId = st.ParentLayerId;
      row.ParentLayerFullPath = st.ParentLayerFullPath ?? "";

      // ✅ 1.3.4：恢复“模型模式”绑定信息
      row.ModelIds.Clear();
      try
      {
        var mids = UnpackGuidList(st.ModelIdsPacked);
        if (mids != null)
        {
          foreach (var g in mids)
            if (g != Guid.Empty) row.ModelIds.Add(g);
        }
      }
      catch { }

    }

    ApplyBulbVisual(row);
  }

  // 第一行：默认±0行（名称/标高/剖切高度不可编辑）
  void AddBaseRow()
  {
    var row = AddRowInternal();
    ApplyBaseRowPreset(row);

  }

  void ApplyBaseRowPreset(PlanLevelRow row)
  {
    if (row == null) return;
    if (row.IsBaseRow) return;

    row.IsBaseRow = true;

    var tbName = row.TxtName as TextBox;
    if (tbName != null)
    {
      tbName.Text = "±0";
      tbName.ReadOnly = true;
      tbName.GotFocus += (s, e) => { try { this.Focus(); } catch { } };
      TryMakeTextBoxFlat(tbName, _rowBack);
    }

    var tbElev = row.TxtElev as TextBox;
    if (tbElev != null)
    {
      tbElev.Text = "±0.000";
      tbElev.ReadOnly = true;
      tbElev.GotFocus += (s, e) => { try { this.Focus(); } catch { } };
      TryMakeTextBoxFlat(tbElev, _rowBack);
    }

    var tbCut = row.TxtCut as TextBox;
    if (tbCut != null)
    {
      tbCut.Text = "——";
      tbCut.ReadOnly = true;
      tbCut.GotFocus += (s, e) => { try { this.Focus(); } catch { } };
      TryMakeTextBoxFlat(tbCut, _rowBack);
    }

    // ✅ ±0 行：绝对标高显示（默认 x.xxx；若已保存过数字则保持该数字）
    var tbAbs = row.TxtAbs as TextBox; // TxtAbs 你现在就是 TextBox
    if (tbAbs != null)
    {
      // 先读一下当前已有内容（可能来自 3dm/内存态恢复）
      double vAbs;
      string absText = GetCtrlText(tbAbs);
      if (TryParseElevNumber(absText, out vAbs))
        tbAbs.Text = vAbs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
      else
        tbAbs.Text = "x.xxx";

      tbAbs.ReadOnly = true; // 保持只读
      tbAbs.GotFocus += (s, e) => { try { this.Focus(); } catch { } };
      TryMakeTextBoxFlat(tbAbs, _rowBack);
    }


    // ✅ ±0 行：不要灯泡，也不要落图按钮（但要占位保持网格对齐）
    row.HasBulb = false;
    row.HasPlotBtn = false;
    row.HasModeBtn = false; 

    // 立刻把这两个按钮变成“空格子”
    var backNow = row.IsSelected ? _rowSelBack : _rowBack;
    MakeButtonAsEmptyCell(row.BtnLight, backNow);
    MakeButtonAsEmptyCell(row.BtnMode,  backNow);
    MakeButtonAsEmptyCell(row.BtnEdit,  backNow);
  }

  string GetCtrlText(Control c)
  {
    if (c == null) return "";
    if (c is TextBox tb) return tb.Text ?? "";
    if (c is Label  lb) return lb.Text ?? "";
    return "";
  }

  // ✅ ±0：通过点取模型点的 Z 值（先换算为“米”），写入“绝对标高”（三位小数）
  void PickBaseAbsFromPoint(PlanLevelRow baseRow)
 {
  if (baseRow == null) return;

  RhinoApp.WriteLine("请点击确定±0高度。");

  var gp = new Rhino.Input.Custom.GetPoint();
  gp.SetCommandPrompt("请点击确定±0高度。");
  gp.Get();

  if (gp.CommandResult() != Rhino.Commands.Result.Success)
    return;

  var pt = gp.Point();        // 点坐标 = 模型单位
  double zModel = pt.Z;

  // —— 单位换算：模型单位 -> 米 ——
  double zMeter = zModel;
  var doc = RhinoDoc.ActiveDoc;

    // ✅ 每次打开面板先清一次±0缓存，避免跨文档/跨Run串值
    PlanGenState.HasEditedBaseAbs = false;
    PlanGenState.BaseAbsValue = 0.0;

  if (doc != null)
  {
    try
    {
      double s2m = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters); // from -> to
      if (!double.IsNaN(s2m) && !double.IsInfinity(s2m) && s2m > 0.0)
        zMeter = zModel * s2m;
    }
    catch
    {
      // 兜底：无法识别单位时，按“已是米”处理
      zMeter = zModel;
    }
  }

  // ✅ 存 5 位（米）
  double abs5 = Math.Round(zMeter, 5, MidpointRounding.AwayFromZero);

  PlanGenState.abslevel = abs5;          // ✅ 你要的变量
  PlanGenState.BaseAbsValue = abs5;      // 兼容旧逻辑（别处可能还用 BaseAbsValue）
  PlanGenState.HasEditedBaseAbs = true;
  PlanGenState.BtnGenBackColor = _baseAbsOkBack;

  // ✅ 显示仍然 3 位（米）
  string s3 = abs5.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
  SetCtrlText(baseRow.TxtAbs, s3);

  // ✅ ±0 变了：所有行的“标高/绝对标高”都要跟着重算与刷新
  RefreshAllAbsCellsFromClip();

  // ✅ 建议：立即保存一次，避免用户不关闭面板就以为已写入（可选但强烈推荐）
  SaveRowsAndWindowState();

  // 刷新视觉
  ApplyRowSelectionVisual(baseRow);
  this.Invalidate();
 }

  const string ClipLayerName = "ClippingPlan";
  const string ClipLayerNameLegacy = "ClippingPlan_PandaBim";


  // ✅ 面板生命周期控制：打开解锁 / 关闭锁定（只作用于 ClippingPlan）
  void UnlockClippingPlanLayerForPanel()
  {
   var doc = RhinoDoc.ActiveDoc;
   if (doc == null) return;

   bool wasLocked;
   int li = EnsureClipTopLayer(doc, out wasLocked);
   if (li < 0 || li >= doc.Layers.Count) return;

   try
   {
    var lay = doc.Layers[li];
    if (lay != null && lay.IsLocked)
    {
      lay.IsLocked = false;
      lay.CommitChanges();
    }
   }
   catch { }
 }

  void LockClippingPlanLayerForPanel()
 {
   var doc = RhinoDoc.ActiveDoc;
   if (doc == null) return;

   try
   {
    int li = FindClipLayerIndexNoCreate(doc);
    if (li < 0 || li >= doc.Layers.Count) return;

    var lay = doc.Layers[li];
    if (lay == null) return;

    if (!lay.IsLocked)
    {
      lay.IsLocked = true;
      lay.CommitChanges();
    }
   }
   catch { }
 }

  void ShowAllClippingPlanesOnClipLayer()
 {
  SetAllClippingPlanesVisibleOnClipLayer(true);
 }

  void HideAllClippingPlanesOnClipLayer()
 {
  SetAllClippingPlanesVisibleOnClipLayer(false);
 }

  void SetAllClippingPlanesVisibleOnClipLayer(bool visible)
  {
   var doc = RhinoDoc.ActiveDoc;
   if (doc == null) return;

   int li = FindClipLayerIndexNoCreate(doc);
   if (li < 0 || li >= doc.Layers.Count) return;

   Layer lay = null;
   try { lay = doc.Layers[li]; } catch { lay = null; }
   if (lay == null) return;

   RhinoObject[] objs = null;
   try { objs = doc.Objects.FindByLayer(lay); } catch { }
   if (objs == null || objs.Length == 0) return;

   foreach (var ro in objs)
   {
     if (ro == null) continue;
     if (ro.ObjectType != ObjectType.ClipPlane) continue;

     try
     {
       var a = ro.Attributes?.Duplicate();
       if (a == null) continue;

       if (a.Visible != visible)
       {
         a.Visible = visible;
         doc.Objects.ModifyAttributes(ro, a, true);
       }
     }
     catch { }
   }

   try { doc.Views.Redraw(); } catch { }
  }


  
  // ✅ 1.4.1：ClippingPlan 图层应与 PandaBim-Plan 同一父级（作为子层），且无视父级路径可被稳定找到
  Layer FindBestPlanOutTopLayerForClip(RhinoDoc doc)
  {
    if (doc == null) return null;

    try
    {
      Layer top = null;
      try { top = doc.Layers.FindName(PlanOutTopLayerName); } catch { top = null; }
      if (top != null && !top.IsDeleted) return top;

      string sep = Layer.PathSeparator;

      // fullpath 列表（用于统计子层数量）
      var fps = new List<string>();
      try
      {
        int n = doc.Layers.Count;
        for (int i = 0; i < n; i++)
        {
          var ly = doc.Layers[i];
          if (ly == null || ly.IsDeleted) continue;
          string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
          if (!string.IsNullOrWhiteSpace(fp)) fps.Add(fp);
        }
      }
      catch { }

      Layer best = null;
      int bestScore = int.MinValue;

      int c = doc.Layers.Count;
      for (int i = 0; i < c; i++)
      {
        var ly = doc.Layers[i];
        if (ly == null || ly.IsDeleted) continue;

        string nm = (ly.Name ?? "").Trim();
        if (!string.Equals(nm, PlanOutTopLayerName, StringComparison.Ordinal)) continue;

        string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fp)) continue;

        int score = 0;

        // 被整理进父级图层时，FullPath 会更深
        if (fp.IndexOf(sep, StringComparison.Ordinal) >= 0) score += 5;

        // 子层越多越可能是“正在用的那棵”
        string prefix = fp + sep;
        int child = 0;
        for (int k = 0; k < fps.Count; k++)
          if (fps[k].StartsWith(prefix, StringComparison.Ordinal)) child++;

        score += Math.Min(child, 100);

        if (best == null || score > bestScore)
        { best = ly; bestScore = score; }
      }

      return best;
    }
    catch { return null; }
  }

  Layer FindBestClipLayerNoCreate(RhinoDoc doc, Guid targetParentId)
  {
    if (doc == null) return null;

    string sep = Layer.PathSeparator;

    Layer best = null;
    int bestScore = int.MinValue;
    bool foundNew = false;

    // 先扫新名；若没有，再扫旧名
    for (int pass = 0; pass < 2; pass++)
    {
      string want = (pass == 0) ? ClipLayerName : ClipLayerNameLegacy;
      if (pass == 1 && foundNew) break;

      int c = doc.Layers.Count;
      for (int i = 0; i < c; i++)
      {
        var ly = doc.Layers[i];
        if (ly == null || ly.IsDeleted) continue;

        string nm = (ly.Name ?? "").Trim();
        if (!string.Equals(nm, want, StringComparison.Ordinal)) continue;

        if (pass == 0) foundNew = true;

        string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
        int score = 0;

        // parent match 优先（作为 PandaBim-Plan 的子图层）
        try
        {
          if (ly.ParentLayerId == targetParentId) score += 100;
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(fp) && fp.IndexOf(sep, StringComparison.Ordinal) >= 0) score += 1;

        // clipplane 数量（轻微加分）
        int cp = 0;
        try
        {
          var objs = doc.Objects.FindByLayer(ly);
          if (objs != null)
          {
            foreach (var ro in objs)
              if (ro != null && ro.ObjectType == ObjectType.ClipPlane) cp++;
          }
        }
        catch { }
        score += Math.Min(cp, 20);

        if (best == null || score > bestScore)
        { best = ly; bestScore = score; }
      }
    }

    return best;
  }

  int FindClipLayerIndexNoCreate(RhinoDoc doc)
  {
    if (doc == null) return -1;

    try
    {
      var planTop = FindBestPlanOutTopLayerForClip(doc);

      Guid targetParentId = Guid.Empty;
      try
      {
        if (planTop != null && !planTop.IsDeleted)
          targetParentId = planTop.Id;
      }
      catch { targetParentId = Guid.Empty; }

      // 校验父级是否有效
      try
      {
        if (targetParentId != Guid.Empty)
        {
          var p = doc.Layers.FindId(targetParentId);
          if (p == null || p.IsDeleted) targetParentId = Guid.Empty;
        }
      }
      catch { targetParentId = Guid.Empty; }

      var lay = FindBestClipLayerNoCreate(doc, targetParentId);
      if (lay == null) return -1;
      return lay.Index;
    }
    catch { return -1; }
  }


  int EnsureClipTopLayer(RhinoDoc doc, out bool wasLocked)
 {
  // ✅ 1.4.1：ClippingPlan 不再强制在最外层；应挂在 PandaBim-Plan 图层内（作为其子图层），且会随 PandaBim-Plan 的移动而移动
  // ✅ 同时兼容旧名 ClippingPlan_PandaBim（自动改名为 ClippingPlan，避免产生两套）
  wasLocked = false;
  if (doc == null) return -1;

  // 目标父级：PandaBim-Plan 的父级（子层）
  Layer planTop = null;
  try { planTop = FindBestPlanOutTopLayerForClip(doc); } catch { planTop = null; }

  // ✅ 若 PandaBim-Plan 尚不存在，则创建顶层（ClippingPlan 作为其子图层需要父层存在）
  if (planTop == null || planTop.IsDeleted)
  {
    try
    {
      int liTop = doc.Layers.Add(new Layer { Name = PlanOutTopLayerName });
      if (liTop >= 0 && liTop < doc.Layers.Count) planTop = doc.Layers[liTop];
    }
    catch { planTop = null; }

    // 兜底再找一次
    if (planTop == null)
    {
      try { planTop = FindBestPlanOutTopLayerForClip(doc); } catch { planTop = null; }
    }
  }

  Guid targetParentId = Guid.Empty;
  try
  {
    if (planTop != null && !planTop.IsDeleted)
      targetParentId = planTop.Id;
  }
  catch { targetParentId = Guid.Empty; }

  // 校验父级是否有效
  try
  {
    if (targetParentId != Guid.Empty)
    {
      var p = doc.Layers.FindId(targetParentId);
      if (p == null || p.IsDeleted) targetParentId = Guid.Empty;
    }
  }
  catch { targetParentId = Guid.Empty; }

  // 找现有 clip 层（新名优先；无则旧名）
  Layer lay = null;
  try { lay = FindBestClipLayerNoCreate(doc, targetParentId); } catch { lay = null; }

  int li = (lay != null) ? lay.Index : -1;

  // 旧名 => 新名（仅当文档里没有新名 clip 层时）
  try
  {
    if (lay != null)
    {
      string nm = (lay.Name ?? "").Trim();
      if (string.Equals(nm, ClipLayerNameLegacy, StringComparison.Ordinal))
      {
        bool hasNew = false;
        try
        {
          int c = doc.Layers.Count;
          for (int i = 0; i < c; i++)
          {
            var ly = doc.Layers[i];
            if (ly == null || ly.IsDeleted) continue;
            string n2 = (ly.Name ?? "").Trim();
            if (string.Equals(n2, ClipLayerName, StringComparison.Ordinal))
            { hasNew = true; break; }
          }
        }
        catch { hasNew = false; }

        if (!hasNew)
        {
          lay.Name = ClipLayerName;
          lay.CommitChanges();
        }
      }
    }
  }
  catch { }

  // 若已存在：对齐父级（随 PandaBim-Plan 移动）
  if (li >= 0 && li < doc.Layers.Count && lay != null)
  {
    try
    {
      if (lay.ParentLayerId != targetParentId)
      {
        lay.ParentLayerId = targetParentId;
        lay.CommitChanges();
      }
    }
    catch { }
  }

  // 不存在则创建（直接挂在目标父级下）
  if (li < 0)
  {
    var nl = new Layer
    {
      Name = ClipLayerName,
      ParentLayerId = targetParentId
      // IsLocked：由用户/文档自行决定；此处不做任何锁定设定
    };
    li = doc.Layers.Add(nl);
  }

  // 仅记录当前锁定状态（不改动）
  if (li >= 0 && li < doc.Layers.Count)
    wasLocked = doc.Layers[li].IsLocked;

  return li;
 }

  Guid FindExistingClipByNameOnLayer(RhinoDoc doc, int layerIndex, string objName)
 {
  if (doc == null) return Guid.Empty;
  if (layerIndex < 0) return Guid.Empty;
  if (string.IsNullOrWhiteSpace(objName)) return Guid.Empty;

  foreach (var ro in doc.Objects)
  {
    if (ro == null) continue;
    if (ro.Attributes == null) continue;
    if (ro.Attributes.LayerIndex != layerIndex) continue;
    if (!string.Equals(ro.Attributes.Name ?? "", objName, StringComparison.Ordinal)) continue;

    // 只要是 ClippingPlane 就算命中（避免误删别的）
    if (ro.ObjectType == ObjectType.ClipPlane)
      return ro.Id;
  }
  return Guid.Empty;
 }

   PlanLevelRow GetFirstLightOnRow()
 {
  foreach (var r in _rows)
  {
    if (r == null) continue;
    if (!r.HasBulb) continue;
    if (r.LightOn) return r;
  }
  return null;
 }

  IEnumerable<Rhino.DocObjects.ClippingPlaneObject> EnumClipPlanesOnClipLayer(RhinoDoc doc, int clipLi)
 {
  if (doc == null) yield break;

  foreach (var ro in doc.Objects)
  {
    if (ro == null || ro.Attributes == null) continue;
    if (ro.Attributes.LayerIndex != clipLi) continue;
    if (ro.ObjectType != ObjectType.ClipPlane) continue;

    var cp = ro as Rhino.DocObjects.ClippingPlaneObject;
    if (cp != null) yield return cp;
  }
 }

  Rhino.DocObjects.ClippingPlaneObject FindClipPlaneByNameOnClipLayer(RhinoDoc doc, int clipLi, string name)
 {
  if (doc == null) return null;
  if (clipLi < 0) return null;
  if (string.IsNullOrWhiteSpace(name)) return null;

  foreach (var cp in EnumClipPlanesOnClipLayer(doc, clipLi))
  {
    try
    {
      if (string.Equals(cp.Attributes?.Name ?? "", name, StringComparison.Ordinal))
        return cp;
    }
    catch { }
  }
  return null;
 }

  // ✅ 从 clippingplane 取“绝对标高”（米）
  //   取值：ClippingPlaneGeometry.Plane.OriginZ（模型单位） -> 换算米 -> round 5（存/算）-> 显示 3
  bool TryGetClipAbsMeterByRowName(string rowName, out double absMeter5)
 {
  absMeter5 = 0.0;

  var doc = RhinoDoc.ActiveDoc;
  if (doc == null) return false;
  if (string.IsNullOrWhiteSpace(rowName)) return false;

  bool wasLocked;
  int clipLi = EnsureClipTopLayer(doc, out wasLocked);

  if (clipLi < 0) return false;

  var cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName.Trim());
  if (cp == null) return false;

  double zModel = double.NaN;

  // 1) 正常 RhinoCommon：cp.ClippingPlaneGeometry.Plane
  try
  {
    var geo = cp.ClippingPlaneGeometry;
    zModel = geo.Plane.OriginZ;
  }
  catch { }

  // 2) 兜底：反射拿 Plane（兼容不同 Rhino 版本字段差异）
  if (double.IsNaN(zModel))
  {
    try
    {
      var propGeo = cp.GetType().GetProperty("ClippingPlaneGeometry");
      var geoObj = propGeo?.GetValue(cp, null);
      var pPlane = geoObj?.GetType().GetProperty("Plane");
      if (pPlane != null)
      {
        var pl = (Rhino.Geometry.Plane)pPlane.GetValue(geoObj, null);
        zModel = pl.OriginZ;
      }
    }
    catch { }
  }

  if (double.IsNaN(zModel)) return false;

  // —— 模型单位 -> 米 ——
  double zMeter = zModel;
  try
  {
    double s2m = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
    if (!double.IsNaN(s2m) && !double.IsInfinity(s2m) && s2m > 0.0)
      zMeter = zModel * s2m;
  }
  catch
  {
    zMeter = zModel; // 兜底：按已是米
  }

  absMeter5 = Math.Round(zMeter, 5, MidpointRounding.AwayFromZero);
  return true;
 }

  // ✅ 刷新某一行（非±0）的绝对标高显示：从同名 clippingplane 读值（米，3位）
  void RefreshAbsCellFromClip(PlanLevelRow row)
 {
  if (row == null) return;
  if (row.IsBaseRow) return;

  string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
  if (string.IsNullOrWhiteSpace(rowName))
  {
    SetCtrlText(row.TxtAbs, "x.xxx");
    return;
  }

  double abs5;
  if (TryGetClipAbsMeterByRowName(rowName, out abs5))
  {
    UpdateRowAbsElevFromAbs5(row, abs5);
  }

  else
  {
    // 没有同名 clippingplane：保持占位
    SetCtrlText(row.TxtAbs, "x.xxx");
  }
 }

  // ✅ 纯粹：只在“改名结束”时同步截平面名字；
  //    若该行尚未创建截平面（旧名找不到），则零副作用直接返回（不创建、不提示）。
  void SyncClippingPlaneNameByRowNameEdit(PlanLevelRow row, string oldNameRaw, string newNameRaw)
 {
  if (row == null) return;
  if (row.IsBaseRow) return;

  var doc = RhinoDoc.ActiveDoc;
  if (doc == null) return;

  string oldName = (oldNameRaw ?? "").Trim();
  string newName = (newNameRaw ?? "").Trim();

  // 统一把行名写成 Trim 后的版本，避免空格导致“找不到同名截平面”
  if (!string.Equals(newNameRaw ?? "", newName, StringComparison.Ordinal))
    SetCtrlText(row.TxtName, newName);

  if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

  // 新名为空：回滚为旧名，避免断链（也避免出现“空名截平面”）
  if (string.IsNullOrWhiteSpace(newName))
  {
    if (!string.IsNullOrWhiteSpace(oldName))
      SetCtrlText(row.TxtName, oldName);
    return;
  }

  // 没旧名：说明不是“从一个有效名字改到另一个”，不做任何事（保持纯粹）
  if (string.IsNullOrWhiteSpace(oldName)) return;

  // 面板打开时理论上已解锁，这里兜底
  UnlockClippingPlanLayerForPanel();

  bool wasLocked;
  int clipLi = EnsureClipTopLayer(doc, out wasLocked);
  if (clipLi < 0) return;

  // 只找“旧名”的截平面（可能历史残留不止一个 → 全部改掉）
  var oldIds = new List<Guid>();
  foreach (var ro in doc.Objects)
  {
    if (ro == null || ro.Attributes == null) continue;
    if (ro.Attributes.LayerIndex != clipLi) continue;
    if (ro.ObjectType != ObjectType.ClipPlane) continue;
    if (!string.Equals(ro.Attributes.Name ?? "", oldName, StringComparison.Ordinal)) continue;
    oldIds.Add(ro.Id);
  }

  // ✅ 关键：该行还没创建截平面 → 什么也不做（不创建、不提示）
  if (oldIds.Count == 0) return;

  // 可选但强烈建议：避免“新名冲突”导致两行指向同一个名字（后续按 name 查会乱）
  var conflict = FindClipPlaneByNameOnClipLayer(doc, clipLi, newName);
  if (conflict != null)
  {
    bool isSameGroup = false;
    foreach (var id in oldIds) { if (id == conflict.Id) { isSameGroup = true; break; } }

    if (!isSameGroup)
    {
      SetCtrlText(row.TxtName, oldName);
      Eto.Forms.MessageBox.Show(this, "名称已被其他截平面占用，请换一个不重复的名称。", "提示");
      return;
    }
  }


  // ✅ 同步：若存在派生剖切截平面（-clip），也一并跟随改名
  string oldClipName = oldName + "-clip";
  string newClipName = newName + "-clip";
  var oldClipIds = new List<Guid>();
  foreach (var ro in doc.Objects)
  {
    if (ro == null || ro.Attributes == null) continue;
    if (ro.Attributes.LayerIndex != clipLi) continue;
    if (ro.ObjectType != ObjectType.ClipPlane) continue;
    if (!string.Equals(ro.Attributes.Name ?? "", oldClipName, StringComparison.Ordinal)) continue;
    oldClipIds.Add(ro.Id);
  }

  if (oldClipIds.Count > 0)
  {
    var conflict2 = FindClipPlaneByNameOnClipLayer(doc, clipLi, newClipName);
    if (conflict2 != null)
    {
      bool isSameGroup2 = false;
      foreach (var id in oldClipIds) { if (id == conflict2.Id) { isSameGroup2 = true; break; } }

      if (!isSameGroup2)
      {
        SetCtrlText(row.TxtName, oldName);
        Eto.Forms.MessageBox.Show(this, "名称已被其他截平面占用，请换一个不重复的名称。", "提示");
        return;
      }
    }
  }

  // 改名 + 标签文本（你已有的函数：会把 Name 写上，并尽力把 Label 设为 Text）
  foreach (var id in oldIds)
    TrySetClippingPlaneLabelAsText(doc, id, newName);

  // ✅ 派生 -clip 同步改名（若存在）
  foreach (var id in oldClipIds)
    TrySetClippingPlaneLabelAsText(doc, id, newClipName);

  // 改名后，绝对标高显示依赖“同名截平面” → 立刻刷新本行
  RefreshAbsCellFromClip(row);

  // 若当前有灯泡亮着，你的剖切同步逻辑是“按名字找截平面” → 改名后立刻重同步
  SyncActiveViewportClippingByBulbs();

  // 若当前行选中，顺手把同名截平面选中（体验更一致）
  SelectClippingPlaneForRow(row);

  try { doc.Views.Redraw(); } catch { }
 }


  // ✅ 打开面板/恢复数据后：批量刷新所有标高行的绝对标高显示
  void RefreshAllAbsCellsFromClip()
 {
  foreach (var r in _rows)
  {
    if (r == null) continue;
    if (r.IsBaseRow) continue;
    RefreshAbsCellFromClip(r);
  }
 }

  // ===================== 1.2.1：ClippingPlane <-> 标高行实时同步 =====================

  void InitClipSyncTimer()
  {
    if (_clipSyncTimer != null) return;

    _clipSyncTimer = new UITimer();
    _clipSyncTimer.Interval = 0.05; // 20Hz（Eto.UITimer 的 Interval 单位为秒）
    _clipSyncTimer.Elapsed += (s, e) =>
    {
      try { FlushPendingAbsUpdates(); } catch { }
    };
  }

  void HookDocEvents()
  {
    if (_docEventsHooked) return;

    try
    {
      RhinoDoc.SelectObjects += OnDocSelectObjects;
      RhinoDoc.BeforeTransformObjects += OnDocBeforeTransformObjects;
      RhinoDoc.AfterTransformObjects += OnDocAfterTransformObjects;
      RhinoDoc.ReplaceRhinoObject += OnDocReplaceRhinoObject;
      RhinoDoc.DeleteRhinoObject += OnDocDeleteRhinoObject;
      _docEventsHooked = true;
    }
    catch { }
  }

  void UnhookDocEvents()
  {
    if (!_docEventsHooked) return;

    try
    {
      RhinoDoc.SelectObjects -= OnDocSelectObjects;
      RhinoDoc.BeforeTransformObjects -= OnDocBeforeTransformObjects;
      RhinoDoc.AfterTransformObjects -= OnDocAfterTransformObjects;
      RhinoDoc.ReplaceRhinoObject -= OnDocReplaceRhinoObject;
      RhinoDoc.DeleteRhinoObject -= OnDocDeleteRhinoObject;
    }
    catch { }

    _docEventsHooked = false;

    try { _clipSyncTimer?.Stop(); } catch { }
    _clipSyncTimerRunning = false;
    try { _xformWatch.Clear(); } catch { }
    try { _pendingAbs5ByRowName.Clear(); } catch { }
    try { _pendingCutMeterByRowName.Clear(); } catch { }
    try { _pendingDeletedClipNames.Clear(); } catch { }
    try { _lastAbs5ByRowName.Clear(); } catch { }
    try { _lastCutMeterByRowName.Clear(); } catch { }
    try { _suppressClipNameSync.Clear(); } catch { }
    try { _activeClipRowName = null; } catch { }
  }

  uint SafeXformEventId(object v)
  {
    try
    {
      if (v is uint uu) return uu;
      if (v is int ii) return (uint)Math.Max(0, ii);
      if (v is long ll) return (uint)Math.Max(0L, ll);
      return Convert.ToUInt32(v);
    }
    catch { return 0u; }
  }

  PlanLevelRow FindRowByTrimmedName(string nameTrim)
  {
    if (string.IsNullOrWhiteSpace(nameTrim)) return null;
    string key = nameTrim.Trim();

    foreach (var r in _rows)
    {
      if (r == null) continue;
      if (r.IsBaseRow) continue;

      string rn = (GetCtrlText(r.TxtName) ?? "").Trim();
      if (string.Equals(rn, key, StringComparison.Ordinal))
        return r;
    }
    return null;
  }

  bool IsClipObjOnClipLayer(RhinoDoc doc, RhinoObject ro, int clipLi)
  {
    if (doc == null || ro == null) return false;
    if (clipLi < 0 || clipLi >= doc.Layers.Count) return false;

    try
    {
      int li = ro.Attributes.LayerIndex;
      return li == clipLi;
    }
    catch { return false; }
  }

  
  // ✅ 读取 clippingplane 的 Plane.Origin（模型单位），用于“非法移动时回滚”与剖切高度换算
  bool TryGetClipPlaneOriginByClipObject(ClippingPlaneObject cp, out Point3d origin)
  {
    origin = Point3d.Unset;
    if (cp == null) return false;

    try
    {
      var geo = cp.ClippingPlaneGeometry;
      origin = geo.Plane.Origin;
      if (origin.IsValid) return true;
    }
    catch { }

    // 兜底：反射拿 Plane（兼容不同 Rhino 版本字段差异）
    try
    {
      var propGeo = cp.GetType().GetProperty("ClippingPlaneGeometry");
      var geoObj = propGeo?.GetValue(cp, null);
      var pPlane = geoObj?.GetType().GetProperty("Plane");
      if (pPlane != null)
      {
        var pl = (Rhino.Geometry.Plane)pPlane.GetValue(geoObj, null);
        origin = pl.Origin;
        return origin.IsValid;
      }
    }
    catch { }

    return false;
  }

  // ✅ name 形如 “<RowName>-clip” 的剖切截平面：拆出 RowName
  bool TrySplitCutClipName(string clipName, out string baseRowName)
  {
    baseRowName = null;
    if (string.IsNullOrWhiteSpace(clipName)) return false;

    string n = clipName.Trim();
    const string Suffix = "-clip";
    if (!n.EndsWith(Suffix, StringComparison.Ordinal)) return false;

    string bn = n.Substring(0, n.Length - Suffix.Length);
    if (string.IsNullOrWhiteSpace(bn)) return false;

    baseRowName = bn.Trim();
    return !string.IsNullOrWhiteSpace(baseRowName);
  }

  // ✅ 6.7：标高截平面拖动 => 若存在同名“<RowName>-clip”，则保持其相对高度（剖切高度）不变
  // ✅ 1.2.8 修复：不再用 delta 累加平移 -clip；改为“绝对定位/幂等对齐”到 base + cut，彻底避免叠加漂移
  void TryFollowMoveCutClipByBaseDelta(RhinoDoc doc, int clipLi, string baseRowName, Vector3d delta, HashSet<string> movedNamesOrNull)
  {
    if (doc == null) return;
    if (clipLi < 0) return;
    if (string.IsNullOrWhiteSpace(baseRowName)) return;

    // tiny 判断：避免浮点噪声触发多余随动
    double d2 = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
    if (d2 < 1e-18) return;

    string baseName = baseRowName.Trim();
    if (string.IsNullOrWhiteSpace(baseName)) return;

    string cutName = baseName + "-clip";

    // 若用户本次已同时选中 base 与 -clip 一起移动，则无需程序再随动，避免翻倍
    if (movedNamesOrNull != null && movedNamesOrNull.Contains(cutName)) return;

    // 仅在“已存在 -clip”时随动（不在此处隐式创建）
    var cpCut = FindClipPlaneByNameOnClipLayer(doc, clipLi, cutName);
    if (cpCut == null) return;

    // 防回环：程序移动 cut 时屏蔽一次 AfterTransform/Replace 回写
    if (_suppressClipNameSync.Contains(cutName)) return;

    // 读取面板的剖切高度（米）；非数字/非法范围则不处理
    var row = FindRowByTrimmedName(baseName);
    if (row == null) return;

    string cutText = (GetCtrlText(row.TxtCut) ?? "").Trim();
    double cutMeter;
    if (!TryParseElevNumber(cutText, out cutMeter)) return;
    if (cutMeter < 0.0 || cutMeter > 40.0) return;

    // ✅ 关键：每次把 -clip 吸回到 “base + cutMeter” 的目标位置（幂等），避免任何叠加漂移
    try
    {
      EnsureCutClippingPlaneForRowByMeter(doc, clipLi, baseName, cutMeter, false);
      _lastFollowMoveUtcTicksByRowName[baseName] = DateTime.UtcNow.Ticks;
    }
    catch { }
  }

// 读取 clippingplane 的绝对标高（米，5位），不走“按名字找对象”，用于高频同步
  bool TryGetClipAbsMeterByClipObject(ClippingPlaneObject cp, RhinoDoc doc, out double absMeter5)
  {
    absMeter5 = 0.0;
    if (cp == null || doc == null) return false;

    double zModel = double.NaN;

    try
    {
      var geo = cp.ClippingPlaneGeometry;
      zModel = geo.Plane.OriginZ;
    }
    catch { }

    if (double.IsNaN(zModel))
    {
      try
      {
        var propGeo = cp.GetType().GetProperty("ClippingPlaneGeometry");
        var geoObj = propGeo?.GetValue(cp, null);
        var pPlane = geoObj?.GetType().GetProperty("Plane");
        if (pPlane != null)
        {
          var pl = (Rhino.Geometry.Plane)pPlane.GetValue(geoObj, null);
          zModel = pl.OriginZ;
        }
      }
      catch { }
    }

    if (double.IsNaN(zModel)) return false;

    double zMeter = zModel;
    try
    {
      double s2m = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
      if (!double.IsNaN(s2m) && !double.IsInfinity(s2m) && s2m > 0.0)
        zMeter = zModel * s2m;
    }
    catch
    {
      zMeter = zModel; // 兜底：按已是米
    }

    absMeter5 = Math.Round(zMeter, 5, MidpointRounding.AwayFromZero);
    return true;
  }

  // 统一的“abs5 -> 更新本行绝对标高 + 相对标高”的逻辑（供刷新/实时同步共用）
  void UpdateRowAbsElevFromAbs5(PlanLevelRow row, double abs5)
  {
    if (row == null) return;
    if (row.IsBaseRow) return;

    // 绝对标高显示（3位）
    string s3 = abs5.ToString("0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture);
    SetCtrlText(row.TxtAbs, s3);

    // 标高 = 本行绝对标高 - ±0绝对标高（abslevel）
    double rel = abs5 - PlanGenState.abslevel;

    // 避免 -0.000
    if (Math.Abs(rel) < 0.0005)
      SetCtrlText(row.TxtElev, "±0.000");
    else
      SetCtrlText(row.TxtElev, rel.ToString("0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture));
  }

  void QueueAbsUpdate(string rowName, double abs5)
  {
    if (string.IsNullOrWhiteSpace(rowName)) return;

    string key = rowName.Trim();
    if (string.IsNullOrWhiteSpace(key)) return;

    _pendingAbs5ByRowName[key] = abs5;

    try
    {
      if (_clipSyncTimer != null && !_clipSyncTimerRunning)
      {
        _clipSyncTimer.Start();
        _clipSyncTimerRunning = true;
      }
    }
    catch { }
  }

  void QueueCutUpdate(string rowName, double cutMeter)
  {
    if (string.IsNullOrWhiteSpace(rowName)) return;

    string key = rowName.Trim();
    if (string.IsNullOrWhiteSpace(key)) return;

    _pendingCutMeterByRowName[key] = cutMeter;

    try
    {
      if (_clipSyncTimer != null && !_clipSyncTimerRunning)
      {
        _clipSyncTimer.Start();
        _clipSyncTimerRunning = true;
      }
    }
    catch { }
  }

  void QueueClipDeleteCheck(string clipName)
  {
    if (string.IsNullOrWhiteSpace(clipName)) return;

    string key = clipName.Trim();
    if (string.IsNullOrWhiteSpace(key)) return;

    try { _pendingDeletedClipNames.Add(key); } catch { }

    try
    {
      if (_clipSyncTimer != null && !_clipSyncTimerRunning)
      {
        _clipSyncTimer.Start();
        _clipSyncTimerRunning = true;
      }
    }
    catch { }
  }


  void FlushPendingAbsUpdates()
  {
    if (_isClosingPanel)
    {
      try { _clipSyncTimer?.Stop(); } catch { }
      _clipSyncTimerRunning = false;
      return;
    }

    bool hasAbs = _pendingAbs5ByRowName.Count > 0;
    bool hasCut = _pendingCutMeterByRowName.Count > 0;
    bool hasDel = _pendingDeletedClipNames.Count > 0;

    if (!hasAbs && !hasCut && !hasDel)
    {
      try { _clipSyncTimer?.Stop(); } catch { }
      _clipSyncTimerRunning = false;
      return;
    }

    // 复制一份，避免在更新过程中被新增
    Dictionary<string, double> todoAbs = null;
    Dictionary<string, double> todoCut = null;
    HashSet<string> todoDel = null;

    if (hasAbs)
    {
      todoAbs = new Dictionary<string, double>(_pendingAbs5ByRowName, StringComparer.Ordinal);
      _pendingAbs5ByRowName.Clear();
    }

    if (hasCut)
    {
      todoCut = new Dictionary<string, double>(_pendingCutMeterByRowName, StringComparer.Ordinal);
      _pendingCutMeterByRowName.Clear();
    }

    if (hasDel)
    {
      todoDel = new HashSet<string>(_pendingDeletedClipNames, StringComparer.Ordinal);
      _pendingDeletedClipNames.Clear();
    }

    // ---------- Deletes ----------
    if (todoDel != null && todoDel.Count > 0)
    {
      try
      {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
          int clipLiDel = FindClipLayerIndexNoCreate(doc);

          if (clipLiDel >= 0)
          {
            foreach (var delName0 in todoDel)
            {
              string delName = (delName0 ?? "").Trim();
              if (string.IsNullOrWhiteSpace(delName)) continue;

              // 若同名对象仍存在 => Replace/移动导致的“旧对象删除”，忽略
              var still = FindClipPlaneByNameOnClipLayer(doc, clipLiDel, delName);
              if (still != null) continue;

              bool isCutDel = TrySplitCutClipName(delName, out string baseRowNameDel);
              string rowNameDel = isCutDel ? (baseRowNameDel ?? "") : delName;
              rowNameDel = (rowNameDel ?? "").Trim();
              if (string.IsNullOrWhiteSpace(rowNameDel)) continue;

              var rowDel = FindRowByTrimmedName(rowNameDel);
              if (rowDel == null) continue;

              if (isCutDel)
              {
                // 仅删 -clip：剖切高度退回占位；清空 cut 缓存，避免下一 tick 刷回旧值
                try { _pendingCutMeterByRowName.Remove(rowNameDel); } catch { }
                try { _lastCutMeterByRowName.Remove(rowNameDel); } catch { }

                SetCtrlText(rowDel.TxtCut, "x.xxx");

                SyncActiveViewportClippingByBulbs();
                ApplyRowSelectionVisual(rowDel);

                continue;
              }

              // 删 base：自动删除同名 -clip（兜底），并回收 UI（GenEdited=false）
              try
              {
                string cutNameDel = rowNameDel + "-clip";
                DeleteClipPlanesByName(doc, clipLiDel, cutNameDel);
              }
              catch { }

              // 清空缓存，避免回刷
              try { _pendingAbs5ByRowName.Remove(rowNameDel); } catch { }
              try { _lastAbs5ByRowName.Remove(rowNameDel); } catch { }
              try { _pendingCutMeterByRowName.Remove(rowNameDel); } catch { }
              try { _lastCutMeterByRowName.Remove(rowNameDel); } catch { }

              rowDel.GenEdited = false;
              rowDel.LightOn = false;

              SetCtrlText(rowDel.TxtCut, "x.xxx");
              SetCtrlText(rowDel.TxtAbs, "x.xxx");

              ApplyBulbVisual(rowDel);
              SyncActiveViewportClippingByBulbs();
              ApplyRowSelectionVisual(rowDel);
            }

            try { SaveRowsAndWindowState(); } catch { }
            try { doc.Views.Redraw(); } catch { }
          }
        }
      }
      catch { }
    }

    // ---------- Abs / Elev ----------
    if (todoAbs != null)
    {
      foreach (var kv in todoAbs)
      {
        string rowName = kv.Key;
        double abs5 = kv.Value;

        var row = FindRowByTrimmedName(rowName);
        if (row == null) continue;

        // 如果该行正在编辑标高输入框，先不打断用户输入：留到下一次 tick 再刷
        var elevTb = row.TxtElev as TextBox;
        if (elevTb != null && !elevTb.ReadOnly)
        {
          _pendingAbs5ByRowName[rowName] = abs5;
          continue;
        }

        UpdateRowAbsElevFromAbs5(row, abs5);
        _lastAbs5ByRowName[rowName] = abs5;
      }
    }

    // ---------- Cut ----------
    if (todoCut != null)
    {
      foreach (var kv in todoCut)
      {
        string rowName = kv.Key;
        double cutMeter = kv.Value;

        var row = FindRowByTrimmedName(rowName);
        if (row == null) continue;

        // 如果该行正在编辑剖切高度输入框，先不打断用户输入：留到下一次 tick 再刷
        var cutTb = row.TxtCut as TextBox;
        if (cutTb != null && !cutTb.ReadOnly)
        {
          _pendingCutMeterByRowName[rowName] = cutMeter;
          continue;
        }

        // 剖切高度显示：米，3位；严格非负
        if (cutMeter < 0.0 && cutMeter > -1e-6) cutMeter = 0.0;
        if (cutMeter < 0.0) cutMeter = 0.0;

        string show = cutMeter.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        SetCtrlText(row.TxtCut, show);

        _lastCutMeterByRowName[rowName] = cutMeter;
      }
    }

    // 若两类都没有待刷了，就停定时器
    if (_pendingAbs5ByRowName.Count == 0 && _pendingCutMeterByRowName.Count == 0 && _pendingDeletedClipNames.Count == 0)
    {
      try { _clipSyncTimer?.Stop(); } catch { }
      _clipSyncTimerRunning = false;
    }
  }

  // 面板手改标高 -> 移动同名截平面（Z），并回写一遍显示
  bool SyncClippingPlaneZByRowElevEdit(PlanLevelRow row, string oldTextRaw, string newTextRaw)
  {
    if (row == null) return false;
    if (row.IsBaseRow) return false;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return false;

    // 必须先有 ±0
    if (!EnsureBaseAbsIsNumeric())
    {
      // 回滚
      if (!string.IsNullOrWhiteSpace(oldTextRaw))
        SetCtrlText(row.TxtElev, oldTextRaw);
      return false;
    }

    string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
    if (string.IsNullOrWhiteSpace(rowName))
    {
      // 回滚
      if (!string.IsNullOrWhiteSpace(oldTextRaw))
        SetCtrlText(row.TxtElev, oldTextRaw);
      return false;
    }

    double relMeter;
    if (!TryParseElevNumber(newTextRaw ?? "", out relMeter))
    {
      // 输入非法：回滚
      if (!string.IsNullOrWhiteSpace(oldTextRaw))
        SetCtrlText(row.TxtElev, oldTextRaw);
      return false;
    }

    double targetAbsMeter = PlanGenState.abslevel + relMeter;

    bool moved = false;
    try
    {
      _suppressClipNameSync.Add(rowName);

      moved = MoveClippingPlaneToAbsMeterByRowName(rowName, targetAbsMeter);

      if (moved)
      {
        // 主动回写：确保立刻显示一致（并格式化 ±0.000 等）
        RefreshAbsCellFromClip(row);
        try { doc.Views.Redraw(); } catch { }
        // ✅ 若本行已有剖切高度数值，则同步更新派生 -clip
        TrySyncCutClippingPlaneFromRowNoAlert(row);
      }
      else
      {
        // 没找到同名截平面：保持用户输入，但绝对标高仍未知
        RhinoApp.WriteLine("[PlanGen] 未找到同名 ClippingPlane，无法按标高移动：{0}", rowName);
      }
    }
    catch { }
    finally
    {
      try { _suppressClipNameSync.Remove(rowName); } catch { }
    }

    return moved;
  }

  bool MoveClippingPlaneToAbsMeterByRowName(string rowName, double targetAbsMeter)
  {
    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return false;
    if (string.IsNullOrWhiteSpace(rowName)) return false;

    bool wasLocked;
    int clipLi = EnsureClipTopLayer(doc, out wasLocked);
    if (clipLi < 0) return false;

    var cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName.Trim());
    if (cp == null) return false;

    double curAbs5;
    if (!TryGetClipAbsMeterByClipObject(cp, doc, out curAbs5))
    {
      // 兜底：按名字读取一次
      if (!TryGetClipAbsMeterByRowName(rowName, out curAbs5))
        return false;
    }

    double deltaAbsMeter = targetAbsMeter - curAbs5;
    if (Math.Abs(deltaAbsMeter) < 1e-8) return true;

    double m2model = 1.0;
    try
    {
      m2model = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
      if (double.IsNaN(m2model) || double.IsInfinity(m2model) || m2model <= 0.0)
        m2model = 1.0;
    }
    catch { m2model = 1.0; }

    double dzModel = deltaAbsMeter * m2model;
    var xform = Rhino.Geometry.Transform.Translation(0.0, 0.0, dzModel);

    Guid newId = Guid.Empty;
    try { newId = doc.Objects.Transform(cp.Id, xform, true); } catch { newId = Guid.Empty; }

    bool moved = newId != Guid.Empty;
    return moved;
  }

  
  // ===================== 剖切截平面（-clip） =====================

  // 仅在“剖切高度（TxtCut）结束编辑”时触发：校验数值并生成/更新/删除派生截平面
  void SyncCutClippingPlaneByRowCutEdit(PlanLevelRow row, string oldTextRaw, string newTextRaw)
  {
    if (row == null) return;
    if (row.IsBaseRow) return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    // 必须先有该行的标高截平面（同名 clippingplane）
    string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
    if (string.IsNullOrWhiteSpace(rowName)) return;

    bool wasLocked = false;
    int clipLi = EnsureClipTopLayer(doc, out wasLocked);
    if (clipLi < 0) return;

    var baseCp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName);
    if (baseCp == null)
    {
      // 该行还没创建标高截平面：不允许编辑剖切高度，回滚并提示
      try { SetCtrlText(row.TxtCut, oldTextRaw ?? ""); } catch { }
      try { Eto.Forms.MessageBox.Show(this, "请先点击➕按钮创建标高层。", "提示"); } catch { }
      return;
    }

    // 统一：确保标高截平面为蓝色（便于与 -clip 区分）
    TrySetClipPlaneObjectColor(doc, baseCp.Id, System.Drawing.Color.Blue);

    string newText = (newTextRaw ?? "").Trim();
    double cutMeter;

    // 非数字：视为“无剖切高度” => 不生成（并删除旧的 -clip）
    if (!TryParseElevNumber(newText, out cutMeter))
    {
      RemoveCutClippingPlaneByRowName(doc, clipLi, rowName);
      try { doc.Views.Redraw(); } catch { }
      return;
    }

    // 校验范围：>=0 且 <=40
    if (cutMeter < 0.0)
    {
      try { SetCtrlText(row.TxtCut, oldTextRaw ?? ""); } catch { }
      try { Eto.Forms.MessageBox.Show(this, "剖切高度不能为负数", "提示"); } catch { }
      return;
    }

    if (cutMeter > 40.0)
    {
      try { SetCtrlText(row.TxtCut, oldTextRaw ?? ""); } catch { }
      try { Eto.Forms.MessageBox.Show(this, "剖切高度单位为“米”", "提示"); } catch { }
      return;
    }

    // 合法：生成/更新 -clip
    bool ok = EnsureCutClippingPlaneForRowByMeter(doc, clipLi, rowName, cutMeter, true);
    if (ok && row.LightOn) SyncActiveViewportClippingByBulbs();
  }

  // ✅ 供其它同步点调用：不弹窗、不回滚，仅在 cut 合法时更新 -clip
  void TrySyncCutClippingPlaneFromRowNoAlert(PlanLevelRow row)
  {
    if (row == null) return;
    if (row.IsBaseRow) return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
    if (string.IsNullOrWhiteSpace(rowName)) return;

    bool wasLocked = false;
    int clipLi = EnsureClipTopLayer(doc, out wasLocked);
    if (clipLi < 0) return;

    string cutText = (GetCtrlText(row.TxtCut) ?? "").Trim();
    double cutMeter;
    if (!TryParseElevNumber(cutText, out cutMeter)) return;
    if (cutMeter < 0.0 || cutMeter > 40.0) return;

    bool ok = EnsureCutClippingPlaneForRowByMeter(doc, clipLi, rowName, cutMeter, true);
    if (ok && row.LightOn) SyncActiveViewportClippingByBulbs();
  }

  // 核心：以“标高截平面（rowName）”为基准，在其上方 cutMeter（米）复制一份 -clip
  bool EnsureCutClippingPlaneForRowByMeter(RhinoDoc doc, int clipLi, string rowName, double cutMeter, bool doRedraw)
  {
    if (doc == null) return false;
    if (clipLi < 0) return false;
    if (string.IsNullOrWhiteSpace(rowName)) return false;

    var baseCp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName);
    if (baseCp == null) return false;

    // 统一：确保标高截平面为蓝色
    TrySetClipPlaneObjectColor(doc, baseCp.Id, System.Drawing.Color.Blue);

    Plane basePlane;
    double width, height;
    if (!TryGetClipPlanePlaneAndSize(doc, baseCp, out basePlane, out width, out height))
      return false;

    // 米 -> 模型单位
    double m2model = 1.0;
    try
    {
      m2model = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
      if (double.IsNaN(m2model) || double.IsInfinity(m2model) || m2model <= 0.0)
        m2model = 1.0;
    }
    catch { m2model = 1.0; }

    double dzModel = cutMeter * m2model;

    Plane clipPlane = basePlane;
    clipPlane.Origin = clipPlane.Origin + Rhino.Geometry.Vector3d.ZAxis * dzModel;

    string clipName = rowName + "-clip";

    Guid id = Guid.Empty;

    // ✅ 若已存在同名 -clip：只平移更新（不重新 Add / 不新增对象）
    var cpOld = FindClipPlaneByNameOnClipLayer(doc, clipLi, clipName);
    if (cpOld != null)
    {
      Guid keepId = cpOld.Id;

      try
      {
        if (TryGetClipPlaneOriginByClipObject(cpOld, out Point3d oldOri))
        {
          var vec = clipPlane.Origin - oldOri;
          if (!vec.IsTiny())
          {
            _suppressClipNameSync.Add(clipName);
            try
            {
              var xform = Rhino.Geometry.Transform.Translation(vec);
              Guid movedId = Guid.Empty;
              try { movedId = doc.Objects.Transform(cpOld.Id, xform, true); } catch { movedId = Guid.Empty; }
              if (movedId != Guid.Empty) keepId = movedId;
            }
            catch { }
            finally
            {
              try { _suppressClipNameSync.Remove(clipName); } catch { }
            }
          }
        }
      }
      catch
      {
        try { _suppressClipNameSync.Remove(clipName); } catch { }
      }

      // ✅ 历史遗留兜底：只保留一个同名 -clip
      DeleteClipPlanesByNameExcept(doc, clipLi, clipName, keepId);

      id = keepId;
    }
    else
    {
      // 不存在则创建：先清理同名旧的 -clip（确保“只有一个副本”）
      DeleteClipPlanesByName(doc, clipLi, clipName);

      var attr = new ObjectAttributes();
      attr.LayerIndex = clipLi;
      attr.Name = clipName;
      attr.ColorSource = ObjectColorSource.ColorFromObject;
      attr.ObjectColor = System.Drawing.Color.Red;

      id = AddClippingPlaneSafe(doc, clipPlane, width, height, attr);
    }

    // 统一：确保 -clip 为红色 + Label(Text)
    if (id != Guid.Empty)
    {
      TrySetClipPlaneObjectColor(doc, id, System.Drawing.Color.Red);
      TrySetClippingPlaneLabelAsText(doc, id, clipName);
    }

    if (doRedraw)
    {
      try { doc.Views.Redraw(); } catch { }
    }

    return id != Guid.Empty;
  }

  // 删除本行派生的 -clip（当 TxtCut 非数字/清空时）
  void RemoveCutClippingPlaneByRowName(RhinoDoc doc, int clipLi, string rowName)
  {
    if (doc == null) return;
    if (clipLi < 0) return;
    if (string.IsNullOrWhiteSpace(rowName)) return;

    string clipName = rowName.Trim() + "-clip";
    DeleteClipPlanesByName(doc, clipLi, clipName);
  }

  // 工具：按 name 删除该图层上的所有 clippingplanes（包含“同名多个”的兜底）
  void DeleteClipPlanesByName(RhinoDoc doc, int clipLi, string name)
  {
    if (doc == null) return;
    if (clipLi < 0) return;
    if (string.IsNullOrWhiteSpace(name)) return;

    var ids = new List<Guid>();
    foreach (var ro in doc.Objects)
    {
      if (ro == null || ro.Attributes == null) continue;
      if (ro.Attributes.LayerIndex != clipLi) continue;
      if (ro.ObjectType != ObjectType.ClipPlane) continue;
      if (!string.Equals(ro.Attributes.Name ?? "", name, StringComparison.Ordinal)) continue;
      ids.Add(ro.Id);
    }

    foreach (var id in ids)
    {
      try { doc.Objects.Delete(id, true); } catch { }
    }
  }

  // 工具：按 name 删除该图层上的所有 clippingplanes（但保留 keepId）
  void DeleteClipPlanesByNameExcept(RhinoDoc doc, int clipLi, string name, Guid keepId)
  {
    if (doc == null) return;
    if (clipLi < 0) return;
    if (string.IsNullOrWhiteSpace(name)) return;

    var ids = new List<Guid>();
    foreach (var ro in doc.Objects)
    {
      if (ro == null || ro.Attributes == null) continue;
      if (ro.Attributes.LayerIndex != clipLi) continue;
      if (ro.ObjectType != ObjectType.ClipPlane) continue;
      if (!string.Equals(ro.Attributes.Name ?? "", name, StringComparison.Ordinal)) continue;
      if (keepId != Guid.Empty && ro.Id == keepId) continue;
      ids.Add(ro.Id);
    }

    foreach (var id in ids)
    {
      try { doc.Objects.Delete(id, true); } catch { }
    }
  }


  // 工具：尽量从 ClippingPlaneSurface 读 plane + 宽高；取不到则 bbox 兜底
  bool TryGetClipPlanePlaneAndSize(RhinoDoc doc, RhinoObject ro, out Plane plane, out double width, out double height)
  {
    plane = Plane.WorldXY;
    width = 0.0;
    height = 0.0;

    if (doc == null) return false;
    if (ro == null) return false;
    if (ro.Geometry == null) return false;

    try
    {
      var cps = ro.Geometry as Rhino.Geometry.ClippingPlaneSurface;
      if (cps != null)
      {
        plane = cps.Plane;

        try { width = cps.Domain(0).Length; } catch { width = 0.0; }
        try { height = cps.Domain(1).Length; } catch { height = 0.0; }

        // bbox 兜底
        if (width <= 1e-9 || height <= 1e-9)
        {
          var bb = cps.GetBoundingBox(true);
          width = Math.Abs(bb.Max.X - bb.Min.X);
          height = Math.Abs(bb.Max.Y - bb.Min.Y);
        }
      }
      else
      {
        var bb = ro.Geometry.GetBoundingBox(true);
        plane = Plane.WorldXY;
        plane.Origin = bb.Center;
        width = Math.Abs(bb.Max.X - bb.Min.X);
        height = Math.Abs(bb.Max.Y - bb.Min.Y);
      }
    }
    catch { }

    // 最终兜底：沿用你 RunPickClippingPlaneForRow 的默认尺寸（若历史/异常导致读不到）
    if (width <= 1e-9 || height <= 1e-9)
    {
      try
      {
        double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
        double half = 25000.0 * mm;
        width = half * 2.0;
        height = half * 2.0;
      }
      catch
      {
        width = 50000.0;
        height = 50000.0;
      }
    }

    return true;
  }

  // 工具：设置对象色（保证与 layer 色区分）
  void TrySetClipPlaneObjectColor(RhinoDoc doc, Guid id, System.Drawing.Color col)
  {
    if (doc == null) return;
    if (id == Guid.Empty) return;

    try
    {
      var ro = doc.Objects.FindId(id);
      if (ro == null) return;

      var a = ro.Attributes.Duplicate();
      a.ColorSource = ObjectColorSource.ColorFromObject;
      a.ObjectColor = col;

      doc.Objects.ModifyAttributes(ro, a, true);
    }
    catch { }
  }




// ===================== 1.3.7：落图（GeoId 同步 + 防重线） =====================

  const string PlanOutTopLayerName  = "PandaBim-Plan";
  const string PlanOutAxisLayerName = "PB-Wall-Axis";


  // 8.1：稳定判定“是否已选择模式”
  bool IsRowModeReadyForPlot(PlanLevelRow row)
  {
    if (row == null) return false;

    // 先看显式标记
    if (!row.ModeEdited) return false;

    int pick = row.ModePick;

    if (pick == (int)ModePickResult.AllDoc)
      return true;

    if (pick == (int)ModePickResult.Layer)
    {
      // 图层模式：必须能定位到父层
      if (row.ParentLayerId != Guid.Empty) return true;

      string fp = (row.ParentLayerFullPath ?? "").Trim();
      // 允许用 FullPath 恢复（比如文档层 ID 变化）
      return !string.IsNullOrWhiteSpace(fp) && !string.Equals(fp, "ALL", StringComparison.OrdinalIgnoreCase);
    }

    if (pick == (int)ModePickResult.Model)
    {
      // 模型模式：必须有绑定对象
      return row.ModelIds != null && row.ModelIds.Count > 0;
    }

    return false;
  }


  // 8.3：优先 -clip，其次 base；读取 Plane（取 ClippingPlaneSurface.Plane）
  bool TryGetRowPlotPlane(RhinoDoc doc, PlanLevelRow row, out RhinoObject clipRo, out Plane plane)
  {
    clipRo = null;
    plane = Plane.WorldXY;

    if (doc == null) return false;
    if (row == null) return false;

    bool wasLocked;
    int clipLi = EnsureClipTopLayer(doc, out wasLocked);
    if (clipLi < 0 || clipLi >= doc.Layers.Count) return false;

    string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
    if (string.IsNullOrWhiteSpace(rowName)) return false;

    // 先找 <Row>-clip
    var cpClip = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName + "-clip");
    var cpBase = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName);

    var cp = cpClip != null ? (RhinoObject)cpClip : (RhinoObject)cpBase;
    if (cp == null) return false;

    Plane p;
    double w, h;
    if (!TryGetClipPlanePlaneAndSize(doc, cp, out p, out w, out h))
      return false;

    clipRo = cp;
    plane = p;
    return true;
  }


  // 8.2.x：可见性判定（被隐藏/所在图层不可见 => 本轮落图视为“已删除”，不参与求交）
  bool IsLayerVisibleEffective(RhinoDoc doc, int li)
  {
    if (doc == null) return true;
    try
    {
      if (li < 0 || li >= doc.Layers.Count) return true;

      Layer ly = null;
      try { ly = doc.Layers[li]; } catch { ly = null; }

      int guard = 0;
      while (ly != null && guard++ < 64)
      {
        try
        {
          if (ly.IsDeleted) return false;
          if (!ly.IsVisible) return false;
        }
        catch { return false; }

        Guid pid = Guid.Empty;
        try { pid = ly.ParentLayerId; } catch { pid = Guid.Empty; }
        if (pid == Guid.Empty) break;

        try { ly = doc.Layers.FindId(pid); } catch { ly = null; }
      }
    }
    catch { return true; }

    return true;
  }

  // ✅ 兼容不同 RhinoCommon 版本：RhinoObject 的 IsHidden 可能是属性/方法，或不存在（用反射读取）
  bool IsRhinoObjectHidden(RhinoObject ro)
  {
    if (ro == null) return true;

    try
    {
      var pi = ro.GetType().GetProperty("IsHidden");
      if (pi != null && pi.CanRead)
      {
        object v = pi.GetValue(ro, null);
        if (v is bool) return (bool)v;
        if (v is int) return ((int)v) != 0;
      }
    }
    catch { }

    try
    {
      var mi = ro.GetType().GetMethod("IsHidden", Type.EmptyTypes);
      if (mi != null)
      {
        object v = mi.Invoke(ro, null);
        if (v is bool) return (bool)v;
        if (v is int) return ((int)v) != 0;
      }
    }
    catch { }

    return false;
  }

  bool IsObjectVisibleForPlot(RhinoDoc doc, RhinoObject ro)
  {
    if (doc == null) return false;
    if (ro == null) return false;

    try
    {
      // 1) 对象本身被 Hide
      if (IsRhinoObjectHidden(ro)) return false;

      // 2) 对象属性可见性
      var at = ro.Attributes;
      if (at != null)
      {
        try { if (!at.Visible) return false; } catch { }
        try
        {
          int li = at.LayerIndex;
          if (li >= 0 && li < doc.Layers.Count)
          {
            if (!IsLayerVisibleEffective(doc, li)) return false;
          }
        }
        catch { }
      }
    }
    catch { }

    return true;
  }


  // 8.2：按模式收集“本行绑定的模型对象”
  List<RhinoObject> CollectRowPlotTargets(RhinoDoc doc, PlanLevelRow row, Guid excludeId)
  {
    var res = new List<RhinoObject>();
    if (doc == null) return res;
    if (row == null) return res;

    var set = new HashSet<Guid>();

    int pick = row.ModePick;

    // 不限图层（或 ParentLayerFullPath=ALL 的兼容）
    if (pick == (int)ModePickResult.AllDoc || string.Equals((row.ParentLayerFullPath ?? "").Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
    {
      try
      {
        foreach (var ro in doc.Objects)
        {
          if (ro == null) continue;
          if (ro.Id == Guid.Empty) continue;
          if (excludeId != Guid.Empty && ro.Id == excludeId) continue;
                    if (!IsObjectVisibleForPlot(doc, ro)) continue;
if (set.Add(ro.Id)) res.Add(ro);
        }
      }
      catch { }
      return res;
    }

    // 图层模式
    if (pick == (int)ModePickResult.Layer)
    {
      Layer parent = null;

      // 优先按 Id
      if (row.ParentLayerId != Guid.Empty)
      {
        try { parent = doc.Layers.FindId(row.ParentLayerId); } catch { parent = null; }
      }

      // 兜底按 FullPath
      if (parent == null)
      {
        string fp = (row.ParentLayerFullPath ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fp))
        {
          try
          {
            foreach (var ly in doc.Layers)
            {
              if (ly == null || ly.IsDeleted) continue;
              string lfp = (ly.FullPath ?? ly.Name ?? "").Trim();
              if (string.Equals(lfp, fp, StringComparison.Ordinal))
              {
                parent = ly;
                break;
              }
            }
          }
          catch { }
        }
      }

      if (parent == null) return res;

      string parentFp = (parent.FullPath ?? parent.Name ?? "").Trim();
      if (string.IsNullOrWhiteSpace(parentFp)) parentFp = parent.Name ?? "";

      string sep = Layer.PathSeparator;
      string prefix = parentFp + sep;

      try
      {
        foreach (var ly in doc.Layers)
        {
          if (ly == null || ly.IsDeleted) continue;

          string lfp = (ly.FullPath ?? ly.Name ?? "").Trim();
          if (string.IsNullOrWhiteSpace(lfp)) continue;

          bool ok = string.Equals(lfp, parentFp, StringComparison.Ordinal) ||
                    lfp.StartsWith(prefix, StringComparison.Ordinal);

          if (!ok) continue;

          RhinoObject[] objs = null;
          try { objs = doc.Objects.FindByLayer(ly); } catch { objs = null; }
          if (objs == null || objs.Length == 0) continue;

          foreach (var ro in objs)
          {
            if (ro == null) continue;
            if (ro.Id == Guid.Empty) continue;
            if (excludeId != Guid.Empty && ro.Id == excludeId) continue;
                        if (!IsObjectVisibleForPlot(doc, ro)) continue;
if (set.Add(ro.Id)) res.Add(ro);
          }
        }
      }
      catch { }

      return res;
    }

    // 模型模式
    if (pick == (int)ModePickResult.Model)
    {
      if (row.ModelIds == null || row.ModelIds.Count == 0) return res;

      foreach (var id in row.ModelIds)
      {
        if (id == Guid.Empty) continue;
        if (excludeId != Guid.Empty && id == excludeId) continue;

        RhinoObject ro = null;
        try { ro = doc.Objects.FindId(id); } catch { ro = null; }
        if (ro == null) continue;

        
        if (!IsObjectVisibleForPlot(doc, ro)) continue;
if (set.Add(ro.Id)) res.Add(ro);
      }

      return res;
    }

    return res;
  }


  // 8.6：确保输出图层 PandaBim-Plan::<RowName>::PB-Wall-Axis
  int EnsurePlanOutputAxisLayer(RhinoDoc doc, string rowName)
  {
    if (doc == null) return -1;

    string rn = SanitizeLayerName(rowName);
    if (string.IsNullOrWhiteSpace(rn)) rn = "Unnamed";

    string sep = Layer.PathSeparator;

    // 0) 顶层
    // ✅ 1.3.9：若用户把 PandaBim-Plan 移入其它父级图层内，仍应复用该层（按 Name 全局查找），避免在最外层重复新建。
    Layer top = null;
    try { top = doc.Layers.FindName(PlanOutTopLayerName); } catch { top = null; }

    // FindName 在某些情况下（例如被移动进父层后）可能找不到；此处按 Name 全局遍历兜底
    if (top == null)
    {
      try
      {
        // 预先建 FullPath 集合，便于在同名多层时择优（优先已有本行子层/轴线层的那一棵）
        var fpSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ly in doc.Layers)
        {
          if (ly == null || ly.IsDeleted) continue;
          string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
          if (!string.IsNullOrWhiteSpace(fp)) fpSet.Add(fp);
        }

        Layer best = null;
        int bestScore = int.MinValue;

        foreach (var ly in doc.Layers)
        {
          if (ly == null || ly.IsDeleted) continue;
          string nm = (ly.Name ?? "").Trim();
          if (!string.Equals(nm, PlanOutTopLayerName, StringComparison.Ordinal)) continue;

          string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
          if (string.IsNullOrWhiteSpace(fp)) continue;

          int score = 0;

          // 优先：已经有本次 rowName 子层
          string rowFp = fp + sep + rn;
          if (fpSet.Contains(rowFp)) score += 10;

          // 更优先：已经有 PB-Wall-Axis 子层
          string axisFp = rowFp + sep + PlanOutAxisLayerName;
          if (fpSet.Contains(axisFp)) score += 20;

          // 次优先：自身层级更深（说明被用户整理进父层）
          if (fp.IndexOf(sep, StringComparison.Ordinal) >= 0) score += 1;

          if (best == null || score > bestScore)
          { best = ly; bestScore = score; }
        }

        if (best != null) top = best;
      }
      catch { top = null; }
    }

    // 若仍未找到，才新建顶层（默认在最外层）
    if (top == null)
    {
      try
      {
        int li = doc.Layers.Add(new Layer { Name = PlanOutTopLayerName });
        if (li >= 0 && li < doc.Layers.Count) top = doc.Layers[li];
      }
      catch { top = null; }
    }

    // 兜底再找一次（兼容部分版本 Add 后层表延迟）
    if (top == null)
    {
      try { top = doc.Layers.FindName(PlanOutTopLayerName); } catch { top = null; }
      if (top == null)
      {
        try
        {
          foreach (var ly in doc.Layers)
          {
            if (ly == null || ly.IsDeleted) continue;
            string nm = (ly.Name ?? "").Trim();
            if (string.Equals(nm, PlanOutTopLayerName, StringComparison.Ordinal))
            { top = ly; break; }
          }
        }
        catch { top = null; }
      }
    }

    if (top == null) return -1;

    string topFp = (top.FullPath ?? top.Name ?? PlanOutTopLayerName).Trim();
    if (string.IsNullOrWhiteSpace(topFp)) topFp = PlanOutTopLayerName;

    // 1) 行子层
    string rowPath = topFp + sep + rn;

    Layer rowLy = null;
    try
    {
      foreach (var ly in doc.Layers)
      {
        if (ly == null || ly.IsDeleted) continue;
        string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
        if (string.Equals(fp, rowPath, StringComparison.Ordinal))
        { rowLy = ly; break; }
      }
    }
    catch { rowLy = null; }

    if (rowLy == null)
    {
      try
      {
        int li = doc.Layers.Add(new Layer { Name = rn, ParentLayerId = top.Id });
        if (li >= 0 && li < doc.Layers.Count) rowLy = doc.Layers[li];
      }
      catch { rowLy = null; }
    }
    if (rowLy == null) return -1;

    // 2) PB-Wall-Axis 子子层
    string axisPath = rowPath + sep + PlanOutAxisLayerName;

    Layer axisLy = null;
    try
    {
      foreach (var ly in doc.Layers)
      {
        if (ly == null || ly.IsDeleted) continue;
        string fp = (ly.FullPath ?? ly.Name ?? "").Trim();
        if (string.Equals(fp, axisPath, StringComparison.Ordinal))
        { axisLy = ly; break; }
      }
    }
    catch { axisLy = null; }

    if (axisLy == null)
    {
      try
      {
        // ✅ PB-Wall-Axis 图层：颜色=96,56,0；打印线宽=No Print（仅在新建时设置，避免覆盖用户后续修改）
        int li = doc.Layers.Add(new Layer { Name = PlanOutAxisLayerName, ParentLayerId = rowLy.Id, Color = System.Drawing.Color.FromArgb(96, 56, 0), PlotWeight = -1.0 });
        if (li >= 0 && li < doc.Layers.Count) axisLy = doc.Layers[li];
      }
      catch { axisLy = null; }
    }

    return axisLy != null ? axisLy.Index : -1;
  }


  string SanitizeLayerName(string s)
  {
    if (string.IsNullOrWhiteSpace(s)) return "";
    string r = s.Trim();
    try
    {
      // 避免把“::”当成路径分隔符造成层级混乱
      string sep = Layer.PathSeparator;
      if (!string.IsNullOrWhiteSpace(sep))
        r = r.Replace(sep, "_");
    }
    catch { }

    // 进一步轻度清理（避免极端字符）
    r = r.Replace("\r", " ").Replace("\n", " ").Trim();
    return r;
  }


  // 8.3：截平面 vs 单个对象 求交线（只做“截平面与对象”的相交）
  List<Curve> IntersectOneObjectWithPlane(RhinoDoc doc, RhinoObject ro, Plane plane, double tol)
  {
    var res = new List<Curve>();
    var hits = IntersectOneObjectWithPlaneEx(doc, ro, plane, tol);
    if (hits == null || hits.Count == 0) return res;

    foreach (var h in hits)
    {
      if (h == null) continue;
      var c = h.Crv;
      if (c == null) continue;
      if (!c.IsValid) continue;
      res.Add(c);
    }

    return res;
  }

  Guid DeriveBlockLeafGeoId(Guid instId, string path, Guid leafId)
  {
    try
    {
      if (instId == Guid.Empty && leafId == Guid.Empty) return Guid.Empty;

      if (path == null) path = "";
      string s = "PB|PlanGen|BlockLeaf|" + instId.ToString("D") + "|" + path + "|" + leafId.ToString("D");
      byte[] bytes = Encoding.UTF8.GetBytes(s);

      using (var md5 = MD5.Create())
      {
        byte[] hash = md5.ComputeHash(bytes);
        if (hash == null || hash.Length < 16) return instId != Guid.Empty ? instId : leafId;

        byte[] g = new byte[16];
        Array.Copy(hash, g, 16);
        return new Guid(g);
      }
    }
    catch
    {
      return instId != Guid.Empty ? instId : leafId;
    }
  }

  // ✅ 1.5.3：兼容不同 RhinoCommon 版本的 Transform 取逆（优先反射调用 TryGetInverse/Invert，
  // 兜底用仿射矩阵手动求逆；用于把世界 plane 变换到 block definition 的局部坐标）。
  bool TryInverseTransform(Transform x, out Transform inv)
  {
    inv = Transform.Identity;

    try
    {
      // 1) TryGetInverse(out Transform)
      try
      {
        var mi = typeof(Transform).GetMethod("TryGetInverse", new Type[] { typeof(Transform).MakeByRefType() });
        if (mi != null)
        {
          object boxed = x;
          object[] args = new object[] { Transform.Identity };
          object ret = mi.Invoke(boxed, args);
          bool ok = (ret is bool) && (bool)ret;
          if (ok)
          {
            try { inv = (Transform)args[0]; return inv.IsValid; }
            catch { return true; }
          }
        }
      }
      catch { }

      // 2) Invert()（部分版本为 bool Invert()，部分可能为 void Invert()）
      try
      {
        var mi2 = typeof(Transform).GetMethod("Invert", Type.EmptyTypes);
        if (mi2 != null)
        {
          object boxed2 = x;
          object ret2 = mi2.Invoke(boxed2, null);
          // 如果返回 bool，则需为 true；如果返回 void，则直接认为成功并取 boxed2
          if (ret2 is bool)
          {
            if ((bool)ret2)
            {
              inv = (Transform)boxed2;
              return inv.IsValid;
            }
          }
          else
          {
            inv = (Transform)boxed2;
            return inv.IsValid;
          }
        }
      }
      catch { }
    }
    catch { }

    // 3) 手动仿射求逆（最后一行应为 0,0,0,1）
    try
    {
      double eps = 1e-12;
      if (Math.Abs(x.M30) > eps || Math.Abs(x.M31) > eps || Math.Abs(x.M32) > eps || Math.Abs(x.M33 - 1.0) > eps)
        return false;

      // 3x3
      double a00 = x.M00, a01 = x.M01, a02 = x.M02;
      double a10 = x.M10, a11 = x.M11, a12 = x.M12;
      double a20 = x.M20, a21 = x.M21, a22 = x.M22;

      double det = a00 * (a11 * a22 - a12 * a21)
                 - a01 * (a10 * a22 - a12 * a20)
                 + a02 * (a10 * a21 - a11 * a20);

      if (Math.Abs(det) <= eps) return false;
      double id = 1.0 / det;

      // 逆矩阵（伴随矩阵 / det）
      double i00 = (a11 * a22 - a12 * a21) * id;
      double i01 = (a02 * a21 - a01 * a22) * id;
      double i02 = (a01 * a12 - a02 * a11) * id;

      double i10 = (a12 * a20 - a10 * a22) * id;
      double i11 = (a00 * a22 - a02 * a20) * id;
      double i12 = (a02 * a10 - a00 * a12) * id;

      double i20 = (a10 * a21 - a11 * a20) * id;
      double i21 = (a01 * a20 - a00 * a21) * id;
      double i22 = (a00 * a11 - a01 * a10) * id;

      // 平移
      double tx = x.M03, ty = x.M13, tz = x.M23;
      double itx = -(i00 * tx + i01 * ty + i02 * tz);
      double ity = -(i10 * tx + i11 * ty + i12 * tz);
      double itz = -(i20 * tx + i21 * ty + i22 * tz);

      inv = Transform.Identity;
      inv.M00 = i00; inv.M01 = i01; inv.M02 = i02; inv.M03 = itx;
      inv.M10 = i10; inv.M11 = i11; inv.M12 = i12; inv.M13 = ity;
      inv.M20 = i20; inv.M21 = i21; inv.M22 = i22; inv.M23 = itz;
      inv.M30 = 0.0; inv.M31 = 0.0; inv.M32 = 0.0; inv.M33 = 1.0;
      return inv.IsValid;
    }
    catch
    {
      inv = Transform.Identity;
      return false;
    }
  }

  void CollectInstanceDefinitionHits(
    RhinoDoc doc,
    InstanceDefinition idef,
    Plane plane,
    double tol,
    Transform xformToWorld,
    Guid rootInstanceId,
    string path,
    List<PlotHitCurve> res)
  {
    if (doc == null) return;
    if (idef == null) return;
    if (res == null) return;

    RhinoObject[] defs = null;
    try { defs = idef.GetObjects(); } catch { defs = null; }
    if (defs == null || defs.Length == 0) return;

    foreach (var def in defs)
    {
      if (def == null) continue;

      // ✅ 1.5.3：Block definition 内对象不要走“图层可见性”判定（ByParent / layer=0 / 嵌套层级容易误杀），
      // 只做对象级的 Visible/Hidden 过滤；实例外层 ro 已经判定过一次可见性。
      bool defObjVisible = true;
      try { if (def.IsHidden) defObjVisible = false; } catch { }
      try { if (def.Attributes != null && !def.Attributes.Visible) defObjVisible = false; } catch { }
      if (!defObjVisible) continue;

      // ✅ 排除线/点/截平面（与外层 targets 一致）
      if (def.ObjectType == ObjectType.Curve) continue;
      if (def.ObjectType == ObjectType.Point) continue;
      if (def.ObjectType == ObjectType.ClipPlane) continue;

      // 嵌套块：递归展开
      var defInst = def as InstanceObject;
      if (defInst != null)
      {
        try
        {
          var subDef = defInst.InstanceDefinition;
          if (subDef == null) continue;

          Transform x2 = xformToWorld;
          try { x2 = xformToWorld * defInst.InstanceXform; } catch { try { x2 = xformToWorld; x2 *= defInst.InstanceXform; } catch { } }

          string seg = "";
          try { seg = def.Id.ToString("D"); } catch { seg = ""; }
          string path2 = path ?? "";
          if (!string.IsNullOrWhiteSpace(seg))
            path2 = string.IsNullOrWhiteSpace(path2) ? seg : (path2 + "/" + seg);

          CollectInstanceDefinitionHits(doc, subDef, plane, tol, x2, rootInstanceId, path2, res);
        }
        catch { }

        continue;
      }

      var g0 = def.Geometry;
      if (g0 == null) continue;

      // ✅ 1.5.3：不变换几何（避免复杂 Brep/SubD Duplicate/Transform 失败），
      // 反过来把世界 plane 变换到 definition 局部坐标求交，再把交线变回世界坐标。
      Plane plocal = plane;
      try
      {
        Transform inv;
        if (TryInverseTransform(xformToWorld, out inv))
          plocal.Transform(inv);
      }
      catch { }

      var part = IntersectGeometryWithPlane(g0, plocal, tol);
      if (part == null || part.Count == 0) continue;

      string srcName = "";
      try { srcName = (def.Attributes?.Name ?? "").Trim(); } catch { srcName = ""; }

      Guid leafId = Guid.Empty;
      try { leafId = def.Id; } catch { leafId = Guid.Empty; }

      // ✅ 1.5.3（阶段1）：测试阶段一个 block 仅用一次，GeoId 直接用“block 内对象自身 Id”
      // 以确保能稳定取到 block 内曲面的 GeoId。
      // （后续若同一块被多次插入，再切回/升级为 DeriveBlockLeafGeoId(instId+path+leafId) 以避免串线。）
      Guid geoId = leafId;

      foreach (var c in part)
      {
        if (c == null) continue;
        if (!c.IsValid) continue;

        // 交线在局部坐标，变回世界坐标
        try { c.Transform(xformToWorld); } catch { }

        res.Add(new PlotHitCurve
        {
          GeoId = geoId,
          BlockId = rootInstanceId,
          BlockPath = (path ?? ""),
          SrcName = srcName,
          Crv = c
        });
      }
    }
  }

  List<PlotHitCurve> IntersectOneObjectWithPlaneEx(RhinoDoc doc, RhinoObject ro, Plane plane, double tol)
  {
    var res = new List<PlotHitCurve>();
    if (doc == null) return res;
    if (ro == null) return res;
    if (ro.Geometry == null) return res;

    // 块实例：把 definition 展开为“叶子几何”（不炸块、不改文档），并把 leaf 的 Name/GeoId 带回去
    var inst = ro as InstanceObject;
    if (inst != null)
    {
      try
      {
        var idef = inst.InstanceDefinition;
        if (idef != null)
        {
          CollectInstanceDefinitionHits(doc, idef, plane, tol, inst.InstanceXform, inst.Id, "", res);
        }
      }
      catch { }

      return res;
    }

    // 普通对象：沿用现有求交，并把 ro.Name / ro.Id 带回去
    var segs = IntersectGeometryWithPlane(ro.Geometry, plane, tol);
    if (segs == null || segs.Count == 0) return res;

    string srcName0 = "";
    try { srcName0 = (ro.Attributes?.Name ?? "").Trim(); } catch { srcName0 = ""; }

    foreach (var c in segs)
    {
      if (c == null) continue;
      if (!c.IsValid) continue;

      res.Add(new PlotHitCurve
      {
        GeoId = ro.Id,
        BlockId = Guid.Empty,
        BlockPath = "",
        SrcName = srcName0,
        Crv = c
      });
    }

    return res;
  }


  List<Curve> IntersectGeometryWithPlane(GeometryBase geo, Plane plane, double tol)
  {
    var res = new List<Curve>();
    if (geo == null) return res;

    try
    {
      // Brep / Surface / Extrusion / SubD => 转 Brep 走 BrepPlane
      Brep brep = geo as Brep;

      if (brep == null)
      {
        var ex = geo as Extrusion;
        if (ex != null)
        {
          try { brep = ex.ToBrep(); } catch { brep = null; }
        }
      }

      if (brep == null)
      {
        var srf = geo as Surface;
        if (srf != null)
        {
          try { brep = srf.ToBrep(); } catch { brep = null; }
        }
      }

      if (brep == null)
      {
        var subd = geo as SubD;
        if (subd != null)
        {
          try { brep = subd.ToBrep(); } catch { brep = null; }
        }
      }

      if (brep != null)
      {
        Curve[] crvs = null;
        Point3d[] pts = null;

        bool ok = false;
        try
        {
          ok = Rhino.Geometry.Intersect.Intersection.BrepPlane(brep, plane, tol, out crvs, out pts);
        }
        catch { ok = false; }

        if (ok && crvs != null)
        {
          foreach (var c in crvs)
          {
            if (c == null) continue;
            if (!c.IsValid) continue;
            res.Add(c);
          }
        }

        return res;
      }

      // Mesh
      var mesh = geo as Mesh;
      if (mesh != null)
      {
        Polyline[] pls = null;
        try { pls = Rhino.Geometry.Intersect.Intersection.MeshPlane(mesh, plane); } catch { pls = null; }

        if (pls != null)
        {
          foreach (var pl in pls)
          {
            try
            {
              if (pl.Count < 2) continue;
              var pc = new PolylineCurve(pl);
              if (pc != null && pc.IsValid) res.Add(pc);
            }
            catch { }
          }
        }

        return res;
      }
    }
    catch { }

    return res;
  }


  // 8.4：识别曲线类型（line/arc/spline）
  string GetAxisTypeFromCurve(Curve crv, double tol)
  {
    if (crv == null) return "spline";

    try
    {
      if (crv.IsLinear(tol)) return "line";
    }
    catch { }

    try
    {
      Arc a;
      if (crv.TryGetArc(out a, tol)) return "arc";
    }
    catch { }

    return "spline";
  }


  // 8.4：从源几何 name 继承信息并改写为 Wallaxis|AxisId=...|Axistype=...
  string BuildAxisNameFromSource(string srcName, Guid curveId, string axisType, Curve crvOnZero, Guid geoId, Guid blockId, string blockPath)
  {
    if (string.IsNullOrWhiteSpace(srcName)) return "";

    string s = srcName.Trim();
    string[] parts = null;
    try { parts = s.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries); } catch { parts = null; }
    if (parts == null || parts.Length == 0) return "";

    string head = (parts[0] ?? "").Trim();
    if (string.Equals(head, "Wall", StringComparison.Ordinal))
      head = "Wallaxis";

    var segs = new List<string>();
    segs.Add(head);

    // AxisId + Axistype（曲线几何类型）
    segs.Add("AxisId=" + curveId.ToString());
    segs.Add("Axistype=" + (string.IsNullOrWhiteSpace(axisType) ? "spline" : axisType));

    // 其它段：完全继承，但去掉旧的 GeoId/AxisId/Axistype/Start/End
    for (int i = 1; i < parts.Length; i++)
    {
      string p = (parts[i] ?? "").Trim();
      if (string.IsNullOrWhiteSpace(p)) continue;

      if (p.StartsWith("GeoId=", StringComparison.OrdinalIgnoreCase)) continue;
      if (IsBlockIdToken(p)) continue;
      if (p.StartsWith("AxisId=", StringComparison.OrdinalIgnoreCase)) continue;
      if (p.StartsWith("Axistype=", StringComparison.OrdinalIgnoreCase)) continue;
      if (p.StartsWith("Start=", StringComparison.OrdinalIgnoreCase)) continue;
      if (p.StartsWith("End=", StringComparison.OrdinalIgnoreCase)) continue;

      segs.Add(p);
    }

    // Start/End（Z 固定 0.000）
    Point3d sp = Point3d.Origin;
    Point3d ep = Point3d.Origin;
    try
    {
      if (crvOnZero != null)
      {
        sp = crvOnZero.PointAtStart;
        ep = crvOnZero.PointAtEnd;
      }
    }
    catch { }

    segs.Add("Start=" + FormatPoint2D(sp));
    segs.Add("End=" + FormatPoint2D(ep));

    // ✅ 1.5.6：BlockId1..n + GeoId（GeoId 仍放在最后）
    AppendBlockPathTokens(segs, blockId, blockPath);
    if (geoId != Guid.Empty)
      segs.Add("GeoId=" + geoId.ToString());

    return string.Join("|", segs.ToArray());
  }

  // ===================== 1.3.7：落图防重线（GeoId + 2D 签名） =====================

  bool TryParseGeoIdFromName(string name, out Guid geoId)
  {
    geoId = Guid.Empty;
    if (string.IsNullOrWhiteSpace(name)) return false;

    string s = name.Trim();
    string[] parts = null;
    try { parts = s.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries); } catch { parts = null; }
    if (parts == null || parts.Length == 0) return false;

    for (int i = 0; i < parts.Length; i++)
    {
      string p = (parts[i] ?? "").Trim();
      if (string.IsNullOrWhiteSpace(p)) continue;

      if (!p.StartsWith("GeoId=", StringComparison.OrdinalIgnoreCase)) continue;

      string v = "";
      try { v = p.Substring("GeoId=".Length).Trim(); } catch { v = ""; }
      if (Guid.TryParse(v, out Guid g) && g != Guid.Empty)
      {
        geoId = g;
        return true;
      }
      return false;
    }
    return false;
  }

    // ✅ 1.5.6：判定一个 token 是否为 BlockId / BlockIdN
  bool IsBlockIdToken(string token)
  {
    if (string.IsNullOrWhiteSpace(token)) return false;
    string p = token.Trim();
    if (p.StartsWith("BlockId=", StringComparison.OrdinalIgnoreCase)) return true;
    if (!p.StartsWith("BlockId", StringComparison.OrdinalIgnoreCase)) return false;

    // BlockId1= / BlockId2= ...
    int eq = p.IndexOf('=');
    if (eq <= 0) return false;

    string head = p.Substring(0, eq); // BlockIdN
    if (head.Length <= "BlockId".Length) return false;

    string num = head.Substring("BlockId".Length);
    if (string.IsNullOrWhiteSpace(num)) return false;

    // 全数字
    for (int i = 0; i < num.Length; i++)
    {
      if (num[i] < '0' || num[i] > '9') return false;
    }
    return true;
  }

  // ✅ 1.5.6：把 blockId1 + blockPath（BlockId2..n）展开写入 Name token
  void AppendBlockPathTokens(List<string> segs, Guid blockId1, string blockPath)
  {
    if (segs == null) return;

    string bp = "";
    try { bp = (blockPath ?? "").Trim(); } catch { bp = ""; }

    // 非块：不写任何 BlockId token
    if (blockId1 == Guid.Empty && string.IsNullOrWhiteSpace(bp)) return;

    // BlockId1
    segs.Add("BlockId1=" + blockId1.ToString("D"));

    if (string.IsNullOrWhiteSpace(bp)) return;

    string[] arr = null;
    try { arr = bp.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries); } catch { arr = null; }
    if (arr == null || arr.Length == 0) return;

    int level = 2;
    for (int i = 0; i < arr.Length; i++)
    {
      string t = (arr[i] ?? "").Trim();
      if (string.IsNullOrWhiteSpace(t)) continue;

      // 尝试规范化为 Guid D
      if (Guid.TryParse(t, out Guid g2))
        segs.Add("BlockId" + level.ToString() + "=" + g2.ToString("D"));
      else
        segs.Add("BlockId" + level.ToString() + "=" + t);

      level++;
    }
  }

  // ✅ 1.5.6：解析 BlockId1..n（支持嵌套块）；兼容旧格式 BlockId=...
  // 返回：blockId1 = 最外层块实例 Id；blockPath = BlockId2..n（用 "/" 分隔；可能为空）
  bool TryParseBlockPathFromName(string name, out Guid blockId1, out string blockPath)
  {
    blockId1 = Guid.Empty;
    blockPath = "";
    if (string.IsNullOrWhiteSpace(name)) return false;

    string s = name.Trim();
    string[] parts = null;
    try { parts = s.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries); } catch { parts = null; }
    if (parts == null || parts.Length == 0) return false;

    // level -> guid
    var map = new SortedDictionary<int, Guid>();

    for (int i = 0; i < parts.Length; i++)
    {
      string p = (parts[i] ?? "").Trim();
      if (string.IsNullOrWhiteSpace(p)) continue;

      if (!p.StartsWith("BlockId", StringComparison.OrdinalIgnoreCase)) continue;

      int eq = p.IndexOf('=');
      if (eq <= 0) continue;

      string head = p.Substring(0, eq).Trim(); // BlockId / BlockIdN
      string val = p.Substring(eq + 1).Trim();
      if (string.IsNullOrWhiteSpace(val)) continue;

      int level = 1;

      if (head.Equals("BlockId", StringComparison.OrdinalIgnoreCase))
      {
        level = 1; // 兼容旧格式
      }
      else
      {
        // BlockIdN
        string num = head.Substring("BlockId".Length);
        if (string.IsNullOrWhiteSpace(num)) continue;

        bool ok = true;
        for (int k = 0; k < num.Length; k++)
        {
          if (num[k] < '0' || num[k] > '9') { ok = false; break; }
        }
        if (!ok) continue;

        int lv;
        if (!int.TryParse(num, out lv)) continue;
        if (lv < 1) continue;
        level = lv;
      }

      if (Guid.TryParse(val, out Guid g))
      {
        map[level] = g;
      }
    }

    if (map.Count == 0) return false;

    if (map.TryGetValue(1, out Guid g1))
      blockId1 = g1;

    // BlockId2..n
    var segs = new List<string>();
    foreach (var kv in map)
    {
      if (kv.Key <= 1) continue;
      try { segs.Add(kv.Value.ToString("D")); } catch { }
    }
    if (segs.Count > 0)
      blockPath = string.Join("/", segs.ToArray());
    else
      blockPath = "";

    return true;
  }

  // ✅ 兼容 1.5.5：只取 BlockId1
  bool TryParseBlockIdFromName(string name, out Guid blockId)
  {
    blockId = Guid.Empty;
    string bp = "";
    if (TryParseBlockPathFromName(name, out Guid b1, out bp))
    {
      blockId = b1;
      return true;
    }
    return false;
  }

string GetCurveSig2DKey(Curve crvOnZero)
  {
    if (crvOnZero == null || !crvOnZero.IsValid) return "";

    Point3d a = Point3d.Origin;
    Point3d b = Point3d.Origin;
    Point3d m = Point3d.Origin;

    try { a = crvOnZero.PointAtStart; } catch { }
    try { b = crvOnZero.PointAtEnd; } catch { }

    try
    {
      double t;
      if (crvOnZero.NormalizedLengthParameter(0.5, out t))
        m = crvOnZero.PointAt(t);
      else
        m = crvOnZero.PointAtNormalizedLength(0.5);
    }
    catch
    {
      try { m = new Point3d(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), 0.0); } catch { }
    }

    // 忽略高度 Z
    a.Z = 0.0;
    b.Z = 0.0;
    m.Z = 0.0;

    // 端点无方向性：按 (X,Y) 排序
    if (a.X > b.X || (RhinoMath.EpsilonEquals(a.X, b.X, 1e-12) && a.Y > b.Y))
    {
      var tmp = a; a = b; b = tmp;
    }

    return FormatPoint2D(a) + "|" + FormatPoint2D(b) + "|" + FormatPoint2D(m);
  }

  string BuildAxisNameBasic(Guid curveId, string axisType, Curve crvOnZero, Guid geoId, Guid blockId, string blockPath)
  {
    if (curveId == Guid.Empty) return "";

    var segs = new List<string>();
    segs.Add("Wallaxis");
    segs.Add("AxisId=" + curveId.ToString());
    segs.Add("Axistype=" + (string.IsNullOrWhiteSpace(axisType) ? "spline" : axisType));

    Point3d sp = Point3d.Origin;
    Point3d ep = Point3d.Origin;
    try
    {
      if (crvOnZero != null)
      {
        sp = crvOnZero.PointAtStart;
        ep = crvOnZero.PointAtEnd;
      }
    }
    catch { }

    segs.Add("Start=" + FormatPoint2D(sp));
    segs.Add("End=" + FormatPoint2D(ep));

    // ✅ 1.5.6：BlockId1..n + GeoId（GeoId 仍放在最后）
    AppendBlockPathTokens(segs, blockId, blockPath);
    if (geoId != Guid.Empty)
      segs.Add("GeoId=" + geoId.ToString());

    return string.Join("|", segs.ToArray());
  }




  string FormatPoint2D(Point3d p)
  {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    string x = p.X.ToString("0.000", inv);
    string y = p.Y.ToString("0.000", inv);
    return x + "," + y + ",0.000";
  }


  void TryApplyCurveAttrs(RhinoDoc doc, Guid id, int layerIndex, string name)
  {
    if (doc == null) return;
    if (id == Guid.Empty) return;

    try
    {
      var ro = doc.Objects.FindId(id);
      if (ro == null) return;

      var a = ro.Attributes?.Duplicate();
      if (a == null) return;

      a.LayerIndex = layerIndex;
      a.ColorSource = ObjectColorSource.ColorFromLayer;

      if (name != null) a.Name = name;

      doc.Objects.ModifyAttributes(ro, a, true);
    }
    catch { }
  }

// ===================== Rhino 事件：选中 / 移动 监听 =====================

  void OnDocSelectObjects(object sender, RhinoObjectSelectionEventArgs e)
  {
    if (_isClosingPanel) return;
    if (!ReferenceEquals(PlanGenState.Panel, this)) return;

    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc == null) return;

      bool wasLocked;
      int clipLi = EnsureClipTopLayer(doc, out wasLocked);
      if (clipLi < 0) return;

      var ros = e?.RhinoObjects;
      if (ros == null || ros.Length == 0) return;

      foreach (var ro in ros)
      {
        var cp = ro as ClippingPlaneObject;
        if (cp == null) continue;
        if (!IsClipObjOnClipLayer(doc, cp, clipLi)) continue;

        string name = (cp.Attributes?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) continue;

        var row = FindRowByTrimmedName(name);
        if (row == null) continue;

        _activeClipRowName = name;
        SelectRow(row);
        break;
      }
    }
    catch { }
  }

  void OnDocBeforeTransformObjects(object sender, RhinoTransformObjectsEventArgs e)
  {
    if (_isClosingPanel) return;
    if (!ReferenceEquals(PlanGenState.Panel, this)) return;

    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc == null) return;

      bool wasLocked;
      int clipLi = EnsureClipTopLayer(doc, out wasLocked);
      if (clipLi < 0) return;

      uint tid = SafeXformEventId(e?.TransformEventId);
      if (tid == 0u) return;

      var objs = e?.Objects;
      if (objs == null || objs.Length == 0) return;

      var list = new List<ClipXformWatchItem>();

      foreach (var ro in objs)
      {
        var cp = ro as ClippingPlaneObject;
        if (cp == null) continue;
        if (!IsClipObjOnClipLayer(doc, cp, clipLi)) continue;

        string name = (cp.Attributes?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) continue;

        // 如果是面板驱动的移动：仍然缓存，但 AfterTransform 会被 suppress 掉
        Point3d oldOri = Point3d.Unset;
        bool hasOldOri = false;
        try { hasOldOri = TryGetClipPlaneOriginByClipObject(cp, out oldOri); } catch { }

        list.Add(new ClipXformWatchItem { Id = cp.Id, Name = name, OldOrigin = oldOri, HasOldOrigin = hasOldOri });
      }

      if (list.Count > 0)
        _xformWatch[tid] = list;
    }
    catch { }
  }

  void OnDocAfterTransformObjects(object sender, RhinoAfterTransformObjectsEventArgs e)
  {
    if (_isClosingPanel) return;
    if (!ReferenceEquals(PlanGenState.Panel, this)) return;

    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc == null) return;

      uint tid = SafeXformEventId(e?.TransformEventId);
      if (tid == 0u) return;

      if (!_xformWatch.TryGetValue(tid, out var list) || list == null || list.Count == 0)
        return;

      _xformWatch.Remove(tid);

            // clip layer index（用于 -clip 反算剖切高度）
      bool wasLocked;
      int clipLi = EnsureClipTopLayer(doc, out wasLocked);


      // ✅ 本次变换涉及的 clippingplane 名称集合（用于避免 base 与 -clip 同时选中时重复随动）
      var movedNames = new HashSet<string>(StringComparer.Ordinal);
      foreach (var wi in list)
      {
        string nn = (wi?.Name ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(nn)) movedNames.Add(nn);
      }

      // ✅ 本次变换每个 clippingplane 的实际位移（用于判断 base 与 -clip 是否“真正一起移动”）
      var movedDeltaByName = new Dictionary<string, Vector3d>(StringComparer.Ordinal);
      try
      {
        foreach (var wi in list)
        {
          if (wi == null) continue;
          if (!wi.HasOldOrigin || !wi.OldOrigin.IsValid) continue;

          string nn = (wi.Name ?? "").Trim();
          if (string.IsNullOrWhiteSpace(nn)) continue;

          Point3d oNow = Point3d.Unset;
          bool gotOriNow = false;

          // 优先：按旧 Id 找（多数 Transform 不会改 Id）
          if (wi.Id != Guid.Empty)
          {
            var roNow = doc.Objects.FindId(wi.Id) as ClippingPlaneObject;
            if (roNow != null && IsClipObjOnClipLayer(doc, roNow, clipLi))
              gotOriNow = TryGetClipPlaneOriginByClipObject(roNow, out oNow);
          }

          // 兜底：按名字找
          if (!gotOriNow)
          {
            var cpNow = FindClipPlaneByNameOnClipLayer(doc, clipLi, nn);
            if (cpNow != null) gotOriNow = TryGetClipPlaneOriginByClipObject(cpNow, out oNow);
          }

          if (gotOriNow && oNow.IsValid)
          {
            movedDeltaByName[nn] = oNow - wi.OldOrigin;
          }
        }
      }
      catch { }


      foreach (var it in list)
      {
        string name = (it?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) continue;

        if (_suppressClipNameSync.Contains(name)) continue;

        // ===== 6.6：剖切截平面（<RowName>-clip）拖动 => 反算剖切高度并回写面板 =====
        if (TrySplitCutClipName(name, out string baseRowName))
        {
          // 需要 row 存在才处理
          var row0 = FindRowByTrimmedName(baseRowName);
          if (row0 == null) continue;

          // ✅ 若本次 base 与 -clip 一起移动：不反算剖切高度（避免把 base 的位移误算进剖切高度）
          //    （两者已一起移动时，相对高度不变；无需回写面板，也避免误触发面板回写导致 -clip “被吸回旧位置”）
          if (movedNames.Contains(baseRowName))
            continue;


          if (clipLi >= 0)
          {
            ClippingPlaneObject cpCut = null;

            // 优先：按旧 Id 找（多数 Transform 不会改 Id）
            if (it != null && it.Id != Guid.Empty)
            {
              cpCut = doc.Objects.FindId(it.Id) as ClippingPlaneObject;
              if (cpCut != null && !IsClipObjOnClipLayer(doc, cpCut, clipLi))
                cpCut = null;
            }

            // 兜底：按名字找
            if (cpCut == null)
              cpCut = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);

            var cpBase = FindClipPlaneByNameOnClipLayer(doc, clipLi, baseRowName);
            if (cpCut == null || cpBase == null) continue;

            Point3d oCut, oBase;
            if (!TryGetClipPlaneOriginByClipObject(cpCut, out oCut)) continue;
            if (!TryGetClipPlaneOriginByClipObject(cpBase, out oBase)) continue;

            double tolModel = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
            double s2m = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);

            // 不能低于标高截平面（可等高）
            if (oCut.Z < oBase.Z - tolModel)
            {
              try { MessageBox.Show(this, "剖切高度不能为负数", "提示"); } catch { }

              // 回滚：平移回“变换前 origin”
              if (it != null && it.HasOldOrigin && it.OldOrigin.IsValid)
              {
                _suppressClipNameSync.Add(name);
                try
                {
                  var cpCur = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);
                  if (cpCur != null && TryGetClipPlaneOriginByClipObject(cpCur, out var curOri))
                  {
                    var vec = it.OldOrigin - curOri;
                    if (!vec.IsTiny())
                    {
                      var xform = Rhino.Geometry.Transform.Translation(vec);
                      doc.Objects.Transform(cpCur.Id, xform, true);
                    }
                  }
                }
                catch { }
                finally { _suppressClipNameSync.Remove(name); }

                // 回滚后：重新计算并回写剖切高度
                try
                {
                  var cpRe = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);
                  if (cpRe != null && TryGetClipPlaneOriginByClipObject(cpRe, out var reOri))
                  {
                    double cm = (reOri.Z - oBase.Z) * s2m;
                    if (cm < 0.0 && cm > -1e-6) cm = 0.0;
                    if (cm < 0.0) cm = 0.0;
                    if (cm > 40.0) cm = 40.0;
                    QueueCutUpdate(baseRowName, cm);
                  }
                }
                catch { }

                try { doc.Views.Redraw(); } catch { }
              }

              continue;
            }

            // 反算剖切高度（米）
            double cutMeter = (oCut.Z - oBase.Z) * s2m;
            if (cutMeter < 0.0 && cutMeter > -1e-6) cutMeter = 0.0;

            if (cutMeter < 0.0)
            {
              try { MessageBox.Show(this, "剖切高度不能为负数", "提示"); } catch { }
              continue;
            }

            // 保持与面板输入限制一致（0~40）
            if (cutMeter > 40.0 + 1e-6)
            {
              try { MessageBox.Show(this, "剖切高度单位为“米”", "提示"); } catch { }

              if (it != null && it.HasOldOrigin && it.OldOrigin.IsValid)
              {
                _suppressClipNameSync.Add(name);
                try
                {
                  var cpCur = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);
                  if (cpCur != null && TryGetClipPlaneOriginByClipObject(cpCur, out var curOri))
                  {
                    var vec = it.OldOrigin - curOri;
                    if (!vec.IsTiny())
                    {
                      var xform = Rhino.Geometry.Transform.Translation(vec);
                      doc.Objects.Transform(cpCur.Id, xform, true);
                    }
                  }
                }
                catch { }
                finally { _suppressClipNameSync.Remove(name); }

                // 回滚后：按旧状态回写（避免面板残留非法值）
                try
                {
                  var cpRe = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);
                  if (cpRe != null && TryGetClipPlaneOriginByClipObject(cpRe, out var reOri))
                  {
                    double cm = (reOri.Z - oBase.Z) * s2m;
                    if (cm < 0.0 && cm > -1e-6) cm = 0.0;
                    if (cm < 0.0) cm = 0.0;
                    if (cm > 40.0) cm = 40.0;
                    QueueCutUpdate(baseRowName, cm);
                  }
                }
                catch { }

                try { doc.Views.Redraw(); } catch { }
              }

              continue;
            }

            // “cut 未变化”则不刷
            if (_lastCutMeterByRowName.TryGetValue(baseRowName, out var lastCut))
            {
              if (Math.Abs(lastCut - cutMeter) < 1e-10) continue;
            }

            QueueCutUpdate(baseRowName, cutMeter);
          }

          continue;
        }

        // ===== 原逻辑：标高截平面移动 => 回写 Abs/Elev =====
        double abs5 = 0.0;
        bool got = false;

        // 优先：按旧 Id 找（多数 Transform 不会改 Id）
        if (it != null && it.Id != Guid.Empty)
        {
          var ro = doc.Objects.FindId(it.Id);
          var cp = ro as ClippingPlaneObject;
          if (cp != null)
          {
            got = TryGetClipAbsMeterByClipObject(cp, doc, out abs5);
          }
        }

        // 兜底：按名字找
        if (!got)
          got = TryGetClipAbsMeterByRowName(name, out abs5);

        if (!got) continue;

        // ===== 6.7：标高截平面拖动 => 同步平移对应的 -clip（保持剖切高度不变）=====
        try
        {
          if (it != null && it.HasOldOrigin)
          {
            Point3d oNew = Point3d.Unset; 
            bool gotOri = false;

            // 优先：按 Id 找（多数 Transform 不会改 Id）
            if (it.Id != Guid.Empty)
            {
              var roNow = doc.Objects.FindId(it.Id) as ClippingPlaneObject;
              if (roNow != null) gotOri = TryGetClipPlaneOriginByClipObject(roNow, out oNew);
            }

            // 兜底：按名字找
            if (!gotOri)
            {
              var cpNow = FindClipPlaneByNameOnClipLayer(doc, clipLi, name);
              if (cpNow != null) gotOri = TryGetClipPlaneOriginByClipObject(cpNow, out oNew);
            }

            if (gotOri && oNew.IsValid)
            {
              var delta = oNew - it.OldOrigin;
              // ✅ 6.7 修复：允许 base 与 -clip 一起移动
              // - 若两者确实一起移动：不做任何处理（保持用户操作结果）
              // - 若用户“选中了两者一起移动”，但 -clip 实际没有跟随：强制把 -clip 吸回到 base + cut（保持相对高度不变）
              string cutName = name + "-clip";
              bool cutAlsoSelected = movedNames.Contains(cutName);

              if (cutAlsoSelected)
              {
                bool movedTogether = false;
                try
                {
                  if (movedDeltaByName.TryGetValue(name, out var dBase) && movedDeltaByName.TryGetValue(cutName, out var dCut))
                  {
                    double tolModel = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
                    double eps = Math.Max(tolModel * 5.0, 1e-6);
                    var dd = dBase - dCut;
                    movedTogether = (dd.X * dd.X + dd.Y * dd.Y + dd.Z * dd.Z) <= (eps * eps);
                  }
                }
                catch { movedTogether = false; }

                if (!movedTogether)
                {
                  // 强制修正：忽略 movedNames（否则 TryFollow 内部会直接 return）
                  TryFollowMoveCutClipByBaseDelta(doc, clipLi, name, delta, null);
                }
              }
              else
              {
                TryFollowMoveCutClipByBaseDelta(doc, clipLi, name, delta, movedNames);
              }
            }
          }
        }
        catch { }

        // “z 未变化”则不刷（abs5 已是 5 位 round）
        if (_lastAbs5ByRowName.TryGetValue(name, out var last5))
        {
          if (Math.Abs(last5 - abs5) < 1e-10) continue;
        }

        QueueAbsUpdate(name, abs5);
      }
    }
    catch { }
  }

  void OnDocReplaceRhinoObject(object sender, RhinoReplaceObjectEventArgs e)
  {
    if (_isClosingPanel) return;
    if (!ReferenceEquals(PlanGenState.Panel, this)) return;

    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc == null) return;

      bool wasLocked;
      int clipLi = EnsureClipTopLayer(doc, out wasLocked);
      if (clipLi < 0) return;

      var roNew = e?.NewRhinoObject as ClippingPlaneObject;
      if (roNew == null) return;
      if (!IsClipObjOnClipLayer(doc, roNew, clipLi)) return;

      string name = (roNew.Attributes?.Name ?? "").Trim();
      if (string.IsNullOrWhiteSpace(name)) return;

      if (_suppressClipNameSync.Contains(name)) return;


      // ✅ 6.7：兜底：若标高截平面发生 Replace（某些移动/编辑会触发），则同步平移其 -clip（保持剖切高度不变）
      try
      {
        // 仅处理“标高截平面”（非 -clip）
        if (!TrySplitCutClipName(name, out _))
        {
          bool skipFollow = false;
          long nowTicks = DateTime.UtcNow.Ticks;
          if (_lastFollowMoveUtcTicksByRowName.TryGetValue(name, out var lastTicks))
          {
            if (Math.Abs(nowTicks - lastTicks) < TimeSpan.FromMilliseconds(300).Ticks)
              skipFollow = true;
          }

          if (!skipFollow)
          {
            var roOld = e?.OldRhinoObject as ClippingPlaneObject;
            if (roOld != null)
            {
              Point3d oOld, oNew;
              if (TryGetClipPlaneOriginByClipObject(roOld, out oOld) && TryGetClipPlaneOriginByClipObject(roNew, out oNew))
              {
                var delta = oNew - oOld;
                TryFollowMoveCutClipByBaseDelta(doc, clipLi, name, delta, null);
              }
            }
          }
        }
      }
      catch { }

      double abs5;
      if (!TryGetClipAbsMeterByClipObject(roNew, doc, out abs5)) return;

      if (_lastAbs5ByRowName.TryGetValue(name, out var last5))
      {
        if (Math.Abs(last5 - abs5) < 1e-10) return;
      }

      QueueAbsUpdate(name, abs5);
    }
    catch { }
  }

  // ✅ 1.3.0：监听 Rhino 删除 clippingplane，收敛行状态
  // 规则：
  //  - 同时删除 base 与 -clip：该行 BtnGen 退回“➕”灰色（GenEdited=false）
  //  - 仅删除 base：自动删除 -clip；并回收 UI（GenEdited=false，TxtCut=x.xxx）
  //  - 仅删除 -clip：该行 TxtCut 恢复为 x.xxx（避免后续随动/重算又把 -clip 重建出来）
  void OnDocDeleteRhinoObject(object sender, RhinoObjectEventArgs e)
  {
    if (_isClosingPanel) return;
    if (!ReferenceEquals(PlanGenState.Panel, this)) return;

    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc == null) return;

      var ro = e?.TheObject;
      if (ro == null) return;
      if (ro.ObjectType != ObjectType.ClipPlane) return;

      // 仅处理本系统 clipping 层（不创建层，避免“删光了又被重建”）
      int li0 = -1;
      try { li0 = ro.Attributes.LayerIndex; } catch { li0 = -1; }
      if (li0 < 0 || li0 >= doc.Layers.Count) return;

      Layer ly0 = null;
      try { ly0 = doc.Layers[li0]; } catch { ly0 = null; }
      if (ly0 == null || ly0.IsDeleted) return;

      string lnm = (ly0.Name ?? "").Trim();
      if (!string.Equals(lnm, ClipLayerName, StringComparison.Ordinal) &&
          !string.Equals(lnm, ClipLayerNameLegacy, StringComparison.Ordinal))
        return;

      string name = (ro.Attributes?.Name ?? "").Trim();
      if (string.IsNullOrWhiteSpace(name)) return;

      // 延迟一帧判定：避免 Replace/移动导致的“旧对象删除”误判
      QueueClipDeleteCheck(name);
    }
    catch { }
  }







   // ✅ 单击行时：若该行有 clippingplane，则把它设为“被选中”
  void SelectClippingPlaneForRow(PlanLevelRow row)
  {
    if (row == null) return;
    if (row.IsBaseRow) return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
    if (string.IsNullOrWhiteSpace(rowName)) return;

    // 面板打开时本来就会解锁；这里做个兜底，避免用户手动锁层导致无法选中
    UnlockClippingPlanLayerForPanel();

    bool wasLocked;
    int clipLi = EnsureClipTopLayer(doc, out wasLocked);
    if (clipLi < 0) return;

    // ✅ 6.3：若存在派生剖切截平面（-clip），优先选中它；否则选中标高截平面
    var cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName + "-clip");
    if (cp == null) cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName);
    if (cp == null) return;

    // 只在本 clippingplane 图层内做“单选”，避免越点越多
    foreach (var other in EnumClipPlanesOnClipLayer(doc, clipLi))
    {
      try { if (other != null) other.Select(false); } catch { }
    }

    try { cp.Select(true); } catch { }

    try { doc.Views.Redraw(); } catch { }
  }


  // ✅ 核心：把“互斥灯泡状态”同步为“当前视图 Views clipped”
  void SyncActiveViewportClippingByBulbs()
 {
  var doc = RhinoDoc.ActiveDoc;
  if (doc == null) return;

  var view = doc.Views.ActiveView;
  if (view == null) return;

  Rhino.Display.RhinoViewport vp = view.ActiveViewport;
  if (vp == null) return;

  bool wasLocked;
  int clipLi = EnsureClipTopLayer(doc, out wasLocked);
  if (clipLi < 0) return;

  // 1) 先把当前视图里：本图层全部 clippingplane 都取消剖切
  foreach (var cp in EnumClipPlanesOnClipLayer(doc, clipLi))
  {
    try { cp.RemoveClipViewport(vp, true); } catch { }
  }

  // 2) 找当前亮灯的标高层，把同名 clippingplane 加到“当前视图 Views clipped”
  var onRow = GetFirstLightOnRow();
  if (onRow != null)
  {
    string rowName = GetCtrlText(onRow.TxtName).Trim();
    // ✅ 6.3：若存在派生剖切截平面（-clip），优先激活它；否则激活标高截平面
    var cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName + "-clip");
    if (cp == null) cp = FindClipPlaneByNameOnClipLayer(doc, clipLi, rowName);
    if (cp != null)
    {
      try { cp.AddClipViewport(vp, true); } catch { }
    }
  }

  doc.Views.Redraw();
 }

  // 用反射调用 AddClippingPlane，避免你 Rhino7/8 不同签名导致编译报错
  Guid AddClippingPlaneSafe(RhinoDoc doc, Plane plane, double width, double height, ObjectAttributes attr)
 {
  if (doc == null) return Guid.Empty;

  var ot = doc.Objects;
  var t = ot.GetType();

  // 1) (Plane, double, double, ObjectAttributes)
  try
  {
    var mi = t.GetMethod("AddClippingPlane", new Type[] { typeof(Plane), typeof(double), typeof(double), typeof(ObjectAttributes) });
    if (mi != null)
    {
      var r = mi.Invoke(ot, new object[] { plane, width, height, attr });
      if (r is Guid g && g != Guid.Empty) return g;
    }
  }
  catch { }

  // 2) (Plane, double, double, Guid) -> 再改 attributes
  try
  {
    var mi = t.GetMethod("AddClippingPlane", new Type[] { typeof(Plane), typeof(double), typeof(double), typeof(Guid) });
    if (mi != null)
    {
      var r = mi.Invoke(ot, new object[] { plane, width, height, Guid.Empty });
      if (r is Guid g && g != Guid.Empty)
      {
        var ro = doc.Objects.FindId(g);
        if (ro != null)
        {
          var a2 = ro.Attributes.Duplicate();
          a2.LayerIndex = attr.LayerIndex;
          a2.Name = attr.Name;
          doc.Objects.ModifyAttributes(ro, a2, false);
        }
        return g;
      }
    }
  }
  catch { }

  return Guid.Empty;
 }

  void TrySetClippingPlaneLabelAsText(RhinoDoc doc, Guid id, string text)
 {
  if (doc == null) return;
  if (id == Guid.Empty) return;

  var ro = doc.Objects.FindId(id);
  if (ro == null) return;

  // 至少把 Name 写上（Rhino 默认很多时候显示 Name）
  try
  {
    var a = ro.Attributes.Duplicate();
    a.Name = text;

    var p = a.GetType().GetProperty("ClippingPlaneLabelStyle");
    if (p != null && p.CanWrite && p.PropertyType.IsEnum)
    {
        object vText = null;
        object vNone = null;

        // 目标：TextFromName（你的原逻辑）
        try { vText = Enum.Parse(p.PropertyType, "TextFromName", true); } catch { }
        if (vText == null) { try { vText = Enum.Parse(p.PropertyType, "Text", true); } catch { } }
        if (vText == null) { try { vText = Enum.Parse(p.PropertyType, "TextDotFromName", true); } catch { } }

        // 关键：先切到 None，再切回 TextFromName，强制 Rhino 重建标签 widget
        try { vNone = Enum.Parse(p.PropertyType, "None", true); } catch { }

        if (vNone != null && vText != null)
        {
            var a0 = ro.Attributes.Duplicate();
            a0.Name = text;
            p.SetValue(a0, vNone, null);
            doc.Objects.ModifyAttributes(ro, a0, true);

            var a1 = ro.Attributes.Duplicate();
            a1.Name = text;
            p.SetValue(a1, vText, null);
            doc.Objects.ModifyAttributes(ro, a1, true);
        }
        else
        {
            if (vText != null) p.SetValue(a, vText, null);
            doc.Objects.ModifyAttributes(ro, a, true);
        }
    }
    else
    {
        doc.Objects.ModifyAttributes(ro, a, true);
    }

    // ✅ 额外兜底：轻量“替换一次几何”来清掉显示缓存（不改变形体）
    // ClippingPlaneSurface 自身就有显示缓存概念（Dispose 会清 display cache）:contentReference[oaicite:1]{index=1}
    try
    {
        var ro2 = doc.Objects.FindId(id);
        if (ro2 != null && ro2.Geometry != null)
        {
            var g = ro2.Geometry.Duplicate();
            if (g != null)
            doc.Objects.Replace(id, g, true); // ✅ 第三个参数：ignoreModes
        }
    }
    catch { }
  }
  catch { }

  // 尝试把 “Label 模式” 切到 Text（不同 Rhino 版本字段名不同，用反射尽力而为）
  try
  {
    var propGeo = ro.GetType().GetProperty("ClippingPlaneGeometry");
    var geo = propGeo?.GetValue(ro, null);
    if (geo == null) return;

    var tg = geo.GetType();

    // 可能存在的“文本内容”
    var pText = tg.GetProperty("Text") ?? tg.GetProperty("LabelText") ?? tg.GetProperty("ClippingPlaneText");
    if (pText != null && pText.CanWrite) pText.SetValue(geo, text, null);

    // 可能存在的“显示模式”
    var pMode = tg.GetProperty("LabelMode") ?? tg.GetProperty("LabelType") ?? tg.GetProperty("LabelStyle");
    if (pMode != null && pMode.CanWrite && pMode.PropertyType.IsEnum)
    {
      // 期望枚举里有 Text
      object enumVal = null;
      try { enumVal = Enum.Parse(pMode.PropertyType, "Text", true); } catch { }
      if (enumVal != null) pMode.SetValue(geo, enumVal, null);
    }

    ro.CommitChanges();
  }
  catch { }
 }

  bool RunPickClippingPlaneForRow(PlanLevelRow row)
 {
  if (row == null) return false;

  var doc = RhinoDoc.ActiveDoc;
  if (doc == null) return false;

  // 必须先有 ±0
  if (!EnsureBaseAbsIsNumeric()) return false;

  string rowName = (GetCtrlText(row.TxtName) ?? "").Trim();
  if (string.IsNullOrWhiteSpace(rowName))
  {
    Eto.Forms.MessageBox.Show(this, "请先填写该行的名称（将作为 ClippingPlane 名称）。", "提示");
    return false;
  }

  // 图层
  bool wasLocked;
  int clipLi = EnsureClipTopLayer(doc, out wasLocked);
  if (clipLi < 0) return false;

  try
  {
    // 先记录旧的（同名可能不止一个），但不要提前删除：Esc 取消时必须零副作用
    var oldIds = new List<Guid>();
    foreach (var ro in doc.Objects)
    {
      if (ro == null || ro.Attributes == null) continue;
      if (ro.Attributes.LayerIndex != clipLi) continue;
      if (ro.ObjectType != ObjectType.ClipPlane) continue;
      if (!string.Equals(ro.Attributes.Name ?? "", rowName, StringComparison.Ordinal)) continue;
      oldIds.Add(ro.Id);
    }

    // 点取：XY 平面跟随鼠标（用 DynamicDraw 画一个平面框做预览）
    double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
    double half = 25000.0 * mm;

    var gp = new GetPoint();
    gp.SetCommandPrompt("点击放置标高面 ClippingPlane（XY平面）");
    gp.DynamicDraw += (s, e) =>
    {
      var pl = Plane.WorldXY;
      pl.Origin = e.CurrentPoint;

      var pts = new Point3d[]
      {
        pl.PointAt(-half, -half),
        pl.PointAt( half, -half),
        pl.PointAt( half,  half),
        pl.PointAt(-half,  half),
        pl.PointAt(-half, -half)
      };

      e.Display.DrawPolyline(pts, System.Drawing.Color.White, 1);
    };

    gp.Get();
    if (gp.CommandResult() != Rhino.Commands.Result.Success)
      return false;

    var pt = gp.Point();

    // 目标平面（XY）
    var plane = new Plane(pt, Vector3d.XAxis, -Vector3d.YAxis);
    plane.Origin = pt;

    Guid id = Guid.Empty;

    // ===== 1) 优先：移动旧截平面（推荐路径）=====
    if (oldIds.Count > 0)
    {
      Guid keepId = oldIds[0];
      var roOld = doc.Objects.FindId(keepId);

      if (roOld != null && roOld.Geometry != null)
      {
        // 尽量用 ClippingPlaneSurface 的 Plane.Origin，取不到就退回 bbox center
        Point3d oldOrigin;
        var cps = roOld.Geometry as Rhino.Geometry.ClippingPlaneSurface;
        if (cps != null) oldOrigin = cps.Plane.Origin;
        else oldOrigin = roOld.Geometry.GetBoundingBox(true).Center;

        var xform = Rhino.Geometry.Transform.Translation(pt - oldOrigin);
        Guid movedId = doc.Objects.Transform(keepId, xform, true); // 返回Guid
        bool moved = movedId != Guid.Empty;
        if (moved)
        {
            id = movedId; // ⚠️ 注意：此时对象Id可能已经变了
        }


        if (moved)
        {
            var roMoved = doc.Objects.FindId(id);
            if (roMoved != null)
          {
            var a2 = roMoved.Attributes.Duplicate();
            a2.LayerIndex = clipLi;
            a2.Name = rowName;
            doc.Objects.ModifyAttributes(roMoved, a2, true);
          }
        }

      }
    }

    // ===== 2) 如果移动失败：删旧建新（兜底）=====
    if (id == Guid.Empty)
    {
      var attr = new ObjectAttributes();
      attr.LayerIndex = clipLi;
      attr.Name = rowName;

      id = AddClippingPlaneSafe(doc, plane, half * 2.0, half * 2.0, attr);
      if (id == Guid.Empty)
      {
        Eto.Forms.MessageBox.Show(this, "ClippingPlane 创建失败（未找到可用的 AddClippingPlane 重载）。", "提示");
        return false;
      }

      // 新建成功后再删旧（避免创建失败导致旧的丢失）
      foreach (var oid in oldIds)
      {
        if (oid == Guid.Empty) continue;
        if (oid == id) continue;
        doc.Objects.Delete(oid, true);
      }
    }
    else
    {
      // 移动成功：清理同名多余旧截平面（保留 keepId）
      foreach (var oid in oldIds)
      {
        if (oid == Guid.Empty) continue;
        if (oid == id) continue;
        doc.Objects.Delete(oid, true);
      }
    }

    // 标签：文本
    TrySetClippingPlaneLabelAsText(doc, id, rowName);

    // ---- 绝对标高/标高刷新（你已经按前面改好了就保留）----
    double s2m = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
    double zMeter = pt.Z * s2m;

    double abs5 = Math.Round(zMeter, 5, MidpointRounding.AwayFromZero);
    SetCtrlText(row.TxtAbs, abs5.ToString("0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture));

    double rel = zMeter - PlanGenState.abslevel;
    if (Math.Abs(rel) < 0.0005) SetCtrlText(row.TxtElev, "±0.000");
    else SetCtrlText(row.TxtElev, rel.ToString("0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture));

    SyncActiveViewportClippingByBulbs();
    doc.Views.Redraw();
    return true;
  }
  finally
  {
    // ✅ 不再对 ClippingPlan 做任何锁定/解锁操作
  }
 }





  

  // ✅ 1.3.4：模型模式：选择需要落图的模型（不含曲线），记录 ObjectId 并绑定到行
  bool RunPickModelsForRow(PlanLevelRow row)
  {
    if (row == null) return false;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return false;

    // 关闭“图层模式”提示窗（避免干扰选择）
    try
    {
      if (_layerModePickForm != null)
      {
        try { _layerModePickForm.Close(); } catch { }
        try { _layerModePickForm.Dispose(); } catch { }
        _layerModePickForm = null;
      }
    }
    catch { }

    try
    {
      // 命令行提示
      try { RhinoApp.WriteLine("请选择需要落图的模型，按“空格”或“回车”确认："); } catch { }

      var go = new GetObject();
      go.SetCommandPrompt("请选择需要落图的模型，按“空格”或“回车”确认：");

      // ✅ “模型（不含曲线）”：尽量覆盖常见实体/面/网格/SubD/块
      go.GeometryFilter = ObjectType.Brep
                        | ObjectType.Surface
                        | ObjectType.Extrusion
                        | ObjectType.Mesh
                        | ObjectType.SubD
                        | ObjectType.InstanceReference;

      go.SubObjectSelect = false;
      go.GroupSelect = true;

      go.EnablePreSelect(true, true);
      go.DeselectAllBeforePostSelect = false;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);

      // 至少选 1 个；0 表示不限制上限
      go.GetMultiple(1, 0);

      if (go.CommandResult() != Rhino.Commands.Result.Success) return false;

      var list = new List<Guid>();
      var hs = new HashSet<Guid>();

      int n = go.ObjectCount;
      for (int i = 0; i < n; i++)
      {
        var rf = go.Object(i);
        if (rf == null) continue;

        Guid id = rf.ObjectId;
        if (id == Guid.Empty) continue;

        var ro = doc.Objects.FindId(id);
        if (ro == null) continue;

        // ✅ 兜底：即使误选进来，也不记录曲线
        if (ro.ObjectType == ObjectType.Curve) continue;

        if (hs.Add(id)) list.Add(id);
      }

      if (list.Count == 0) return false;

      // ✅ 写入到“当前行”
      row.ModelIds.Clear();
      foreach (var id in list)
        if (id != Guid.Empty) row.ModelIds.Add(id);

      row.ModePick = (int)ModePickResult.Model;
      row.ModeEdited = true;

      // ✅ 模型模式不再使用图层绑定
      row.ParentLayerId = Guid.Empty;
      row.ParentLayerFullPath = "";

      ApplyRowSelectionVisual(row);
      SaveRowsAndWindowState();

      try { this.Focus(); } catch { }
      return true;
    }
    catch { return false; }
  }

void SetCtrlText(Control c, string v)
  {
    if (c == null) return;
    if (v == null) v = "";
    if (c is TextBox tb) tb.Text = v;
    else if (c is Label lb) lb.Text = v;
  }

  

  // ✅ 1.3.4：Guid 列表打包/解包（用于“模型模式”持久化）
  string PackGuidList(IEnumerable<Guid> ids)
  {
    try
    {
      if (ids == null) return "";
      var sb = new StringBuilder();
      var hs = new HashSet<Guid>();
      foreach (var g in ids)
      {
        if (g == Guid.Empty) continue;
        if (!hs.Add(g)) continue;
        if (sb.Length > 0) sb.Append(";");
        sb.Append(g.ToString());
      }
      return sb.ToString();
    }
    catch { return ""; }
  }

  List<Guid> UnpackGuidList(string packed)
  {
    var list = new List<Guid>();
    try
    {
      if (string.IsNullOrWhiteSpace(packed)) return list;
      var hs = new HashSet<Guid>();
      var parts = packed.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
      foreach (var p in parts)
      {
        Guid g;
        if (Guid.TryParse((p ?? "").Trim(), out g) && g != Guid.Empty && hs.Add(g))
          list.Add(g);
      }
    }
    catch { }
    return list;
  }

void SaveRowsAndWindowState()
  {
    // 保存±0行绝对标高和按钮颜色（如果已编辑过）
     PlanGenState.HasEditedBaseAbs = false;
    // 通过遍历行来查找±0行（IsBaseRow 标识）
    foreach (var row in _rows)
    {
     if (row != null && row.IsBaseRow)
     {
      double v;
      string absText = GetCtrlText(row.TxtAbs);  // 读取绝对标高文本
      if (TryParseElevNumber(absText, out v))   // 如果是有效的数字
      {
        PlanGenState.BaseAbsValue = v;  // 保存绝对标高值
        PlanGenState.HasEditedBaseAbs = true;
        PlanGenState.BtnGenBackColor = _baseAbsOkBack;  // 保存按钮背景颜色（黄色）
        // 调试：确保数据保存
        RhinoApp.WriteLine("保存绝对标高: " + PlanGenState.BaseAbsValue);
        RhinoApp.WriteLine("保存按钮背景颜色: " + PlanGenState.BtnGenBackColor.ToString());
      }
     }
    }
    PlanGenState.HasLastLocation = true;
    PlanGenState.LastLocation = this.Location;

    PlanGenState.HasLastClientSize = true;
    PlanGenState.LastClientSize = this.ClientSize;

    var doc = RhinoDoc.ActiveDoc;
    if (doc != null) PlanGenState.LastDocSerial = doc.RuntimeSerialNumber;

    // 行数据（灯泡状态）
    var list = new List<PlanGenRowState>();
    foreach (var r in _rows)
    {
      var st = new PlanGenRowState();
      st.Name = GetCtrlText(r.TxtName);
      st.Elev = GetCtrlText(r.TxtElev);
      st.Cut  = GetCtrlText(r.TxtCut);
      st.Abs  = GetCtrlText(r.TxtAbs);
      st.LightOn = r.LightOn; // ✅ 记住灯泡
      st.GenEdited = r.GenEdited;

      // ✅ 1.3.3：保存“图层模式”绑定信息
      st.ModePick = r.ModePick;
      st.ModeEdited = r.ModeEdited;
      st.ParentLayerId = r.ParentLayerId;
      st.ParentLayerFullPath = r.ParentLayerFullPath ?? "";

      st.ModelIdsPacked = PackGuidList(r.ModelIds);
      list.Add(st);
    }
    

    

    PlanGenState.LastRows = list;
    PlanGenState.HasLastPanelState = true;

    // 写入 3dm（PlanGenState.SaveToDoc 也必须同步支持 LightOn，见下方第2段补丁）
    PlanGenState.SaveToDoc(doc);
  }

  void RestoreRowsAndWindowState()
  {
    var doc = RhinoDoc.ActiveDoc;
    // ✅ 注意：±0 的绝对标高/按钮黄灰，必须在“行被恢复出来并 ApplyBaseRowPreset 之后”再同步
    //（原来这里先遍历 _rows 会因为 _rows 仍为空而失效）

  // 1) 优先从 3dm 读
    List<PlanGenRowState> rowsFromDoc;
    bool hasSize, hasLoc;
    Eto.Drawing.Size size;
    Eto.Drawing.Point loc;

    if (PlanGenState.TryLoadFromDoc(doc, out rowsFromDoc, out hasSize, out size, out hasLoc, out loc))
    {
      
// ✅ 兜底：防止某些 3dm 存了异常窗口尺寸，导致面板“白板/内容被压没”
var minSize = this.ClientSize;
if (minSize.Width <= 0 || minSize.Height <= 0) minSize = new Eto.Drawing.Size(1000, 620);

bool IsSizeSane(Eto.Drawing.Size s)
{
  if (s.Width <= 0 || s.Height <= 0) return false;
  if (s.Width < minSize.Width) return false;
  if (s.Height < minSize.Height) return false;
  if (s.Width > 20000 || s.Height > 20000) return false;
  return true;
}

bool RectIntersects(Eto.Drawing.RectangleF a, Eto.Drawing.RectangleF b)
{
  float aRight  = a.X + a.Width;
  float aBottom = a.Y + a.Height;
  float bRight  = b.X + b.Width;
  float bBottom = b.Y + b.Height;
  return !(aRight <= b.X || a.X >= bRight || aBottom <= b.Y || a.Y >= bBottom);
}

bool IsLocationSane(Eto.Drawing.Point p, Eto.Drawing.Size sz)
{
  try
  {
    var win = new Eto.Drawing.RectangleF((float)p.X, (float)p.Y, (float)sz.Width, (float)sz.Height);
    foreach (var sc in Eto.Forms.Screen.Screens)
    {
      var wa = sc.WorkingArea; // RectangleF (float)
      if (RectIntersects(win, wa)) return true;
    }
  }
  catch { }

  // 兜底：防极端数值导致飞窗
  if (p.X < -20000 || p.X > 20000) return false;
  if (p.Y < -20000 || p.Y > 20000) return false;
  return true;
}

Eto.Drawing.Point SafeLocationOnPrimary(Eto.Drawing.Size sz)
{
  try
  {
    var sc = Eto.Forms.Screen.PrimaryScreen;
    if (sc != null)
    {
      var wa = sc.WorkingArea; // RectangleF
      float fx = wa.X + Math.Max(0f, (wa.Width - (float)sz.Width) / 2f);
      float fy = wa.Y + Math.Max(0f, (wa.Height - (float)sz.Height) / 2f);
      return new Eto.Drawing.Point((int)Math.Round(fx), (int)Math.Round(fy));
    }
  }
  catch { }
  return new Eto.Drawing.Point(50, 50);
}

// size：先校验（不合法就用默认最小尺寸）
var finalSize = minSize;
if (hasSize && IsSizeSane(size))
{
  finalSize = size;
}
else
{
  hasSize = false;
  finalSize = minSize;
}

if (hasSize)
{
  PlanGenState.HasLastClientSize = true;
  PlanGenState.LastClientSize = finalSize;
  this.ClientSize = finalSize;
}
else
{
  // 记录默认值（下一次 Save 会把坏 3dm 修复掉）
  PlanGenState.HasLastClientSize = true;
  PlanGenState.LastClientSize = minSize;
  this.ClientSize = minSize;
}

// location：如果飞出屏幕，则放回主屏中间
if (hasLoc)
{
  var finalLoc = loc;
  if (!IsLocationSane(loc, finalSize))
    finalLoc = SafeLocationOnPrimary(finalSize);

  PlanGenState.HasLastLocation = true;
  PlanGenState.LastLocation = finalLoc;
  this.Location = finalLoc;
}

      if (rowsFromDoc != null && rowsFromDoc.Count > 0)
      {
        foreach (var st in rowsFromDoc) AddRow(st);
        if (_rows.Count > 0) ApplyBaseRowPreset(_rows[0]);

        NormalizeExclusiveBulbs(); // ✅ 兜底互斥（含全灭情况）

        // ✅ 恢复后立刻刷新每行视觉（含±0按钮黄/灰）
        foreach (var r in _rows) { if (r != null) ApplyRowSelectionVisual(r); }
        

        // ✅ 同步±0绝对标高缓存（后续其它逻辑可能会用到 BaseAbsValue）
        if (_rows.Count > 0)
        {
          var baseRow = _rows[0];
          double vAbs;
          if (baseRow != null && baseRow.IsBaseRow && TryParseElevNumber(GetCtrlText(baseRow.TxtAbs), out vAbs))
          {
            PlanGenState.HasEditedBaseAbs = true;
            PlanGenState.BaseAbsValue = vAbs;
            PlanGenState.BtnGenBackColor = _baseAbsOkBack;
          }
        }

        return;
      }
    }

    //2) fallback：内存态（同一份文档才恢复）
    if (PlanGenState.HasLastPanelState
        && PlanGenState.LastRows != null
        && PlanGenState.LastRows.Count > 0
        && doc != null
        && PlanGenState.LastDocSerial == doc.RuntimeSerialNumber)
    {
      if (PlanGenState.HasLastLocation) this.Location = PlanGenState.LastLocation;
      if (PlanGenState.HasLastClientSize) this.ClientSize = PlanGenState.LastClientSize;

      foreach (var st in PlanGenState.LastRows) AddRow(st);
      if (_rows.Count > 0) ApplyBaseRowPreset(_rows[0]);

      NormalizeExclusiveBulbs(); // ✅ 兜底互斥（含全灭情况）

        // ✅ 恢复后立刻刷新每行视觉（含±0按钮黄/灰）
        foreach (var r in _rows) { if (r != null) ApplyRowSelectionVisual(r); }
        

        // ✅ 同步±0绝对标高缓存（后续其它逻辑可能会用到 BaseAbsValue）
        if (_rows.Count > 0)
        {
          var baseRow = _rows[0];
          double vAbs;
          if (baseRow != null && baseRow.IsBaseRow && TryParseElevNumber(GetCtrlText(baseRow.TxtAbs), out vAbs))
          {
            PlanGenState.HasEditedBaseAbs = true;
            PlanGenState.BaseAbsValue = vAbs;
            PlanGenState.BtnGenBackColor = _baseAbsOkBack;
          }
        }

        return;
    }

    

    // 3) 都没有：默认给固定±0行
    AddBaseRow();

    // ✅ 默认创建后也刷新一遍视觉（保持与恢复逻辑一致）
    foreach (var r in _rows) { if (r != null) ApplyRowSelectionVisual(r); }

    // ✅ 同步±0绝对标高缓存（后续其它逻辑可能会用到 BaseAbsValue）
    if (_rows.Count > 0)
    {
      var baseRow = _rows[0];
      if (baseRow != null && baseRow.IsBaseRow)
      {
            // ✅ 用 5位存储值反写显示 3位
            SetCtrlText(baseRow.TxtAbs,
            PlanGenState.abslevel.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));

            PlanGenState.BaseAbsValue = PlanGenState.abslevel; // 兼容
            PlanGenState.BtnGenBackColor = _baseAbsOkBack;
      }
    }

  }

  // ================== 你原来的 TryMakeTextBoxFlat / 反射工具函数保持不变 ==================

  
void TryMakeTextBoxFlat(TextBox tb, Color back)
{
  if (tb == null) return;

  object native = null;
  try { native = GetNativeControlObject(tb); } catch { native = null; }
  if (native == null) return;

  bool applied = false;

  try
  {
    Type nt = native.GetType();

    // ===== WPF TextBox =====
    if (IsTypeOrSubclass(nt, "System.Windows.Controls.TextBox"))
    {
      object backBrush = NewWpfBrush(back);
      object fgBrush   = NewWpfBrush(tb.TextColor);
      object thick1    = NewWpfThickness(1.0);

      if (backBrush != null)
      {
        SetProp(native, "BorderBrush", backBrush);
        SetProp(native, "Background", backBrush);
      }
      if (fgBrush != null) SetProp(native, "Foreground", fgBrush);
      if (thick1  != null) SetProp(native, "BorderThickness", thick1);

      SetProp(native, "FocusVisualStyle", null);

      applied = true;
      return;
    }

    // ===== WinForms TextBox =====
    if (IsTypeOrSubclass(nt, "System.Windows.Forms.TextBox"))
    {
      var p = nt.GetProperty("BorderStyle");
      if (p != null && p.CanWrite)
      {
        object noneVal = null;
        try { noneVal = Enum.Parse(p.PropertyType, "None"); } catch { noneVal = null; }
        if (noneVal != null) p.SetValue(native, noneVal, null);
      }

      SetProp(native, "BackColor", NewDrawingColor(back));
      SetProp(native, "ForeColor", NewDrawingColor(tb.TextColor));

      applied = true;
      return;
    }
  }
  catch { applied = false; }

  // fallback：至少保证可见（避免白底 + 浅色字看不见）
  if (!applied)
  {
    try
    {
      tb.TextColor = Colors.Black;
      tb.BackgroundColor = Colors.White;
    }
    catch { }
  }
}


  void BeginEditCell(TextBox tb)
  {
    if (tb == null) return;

    // 结束上一个
    if (_editingCell != null && _editingCell != tb)
      EndEditCell(_editingCell);

    // ✅ 记录编辑前文本（用于判断“是否改名”）
    _editBeginText[tb] = tb.Text ?? "";  

    _editingCell = tb;
    tb.ReadOnly = false;

    try
    {
      tb.Focus();
      tb.SelectAll();
    }
    catch { }
  }

  void EndEditCell(TextBox tb)
  {
   if (tb == null) return;

    // ✅ 取出编辑前文本
    string oldText = "";
    _editBeginText.TryGetValue(tb, out oldText);
    _editBeginText.Remove(tb);

   tb.ReadOnly = true;
   if (_editingCell == tb) _editingCell = null;

   // ✅ 仅当“名称栏（TxtName）结束编辑”时：同步 clippingplane.Name
   try
   {
    if (_tbRowMap.TryGetValue(tb, out var row) && row != null)
    {
      // 只处理“名称列”
      if (!row.IsBaseRow && ReferenceEquals(tb, row.TxtName))
      {
        SyncClippingPlaneNameByRowNameEdit(row, oldText, tb.Text);
      }

      // ✅ 标高栏（TxtElev）结束编辑：驱动同名 clippingplane 上下移动，并回写本行显示
      else if (!row.IsBaseRow && ReferenceEquals(tb, row.TxtElev))
      {
        SyncClippingPlaneZByRowElevEdit(row, oldText, tb.Text);
      }
    
      // ✅ 剖切高度栏（TxtCut）结束编辑：生成/更新/删除同名 "-clip" 副本（派生剖切截平面）
      else if (!row.IsBaseRow && ReferenceEquals(tb, row.TxtCut))
      {
        SyncCutClippingPlaneByRowCutEdit(row, oldText, tb.Text);
      }

}
   }
   catch { }

   // ✅ 用 AsyncInvoke：等鼠标事件/焦点分配走完后再强制清焦点（WPF 尤其需要）
   try
   {
    Eto.Forms.Application.Instance.AsyncInvoke(() =>
    {
      try { this.Focus(); } catch { }
      ClearKeyboardFocusNative(); // WPF: Keyboard.ClearFocus()
    });
   }
    catch
   {
    // 兜底
    try { this.Focus(); } catch { }
    ClearKeyboardFocusNative();
   }
  }



  void RegisterCellTextBox(TextBox tb, Func<bool> canEdit)
  {
    if (tb == null) return;

    // 默认锁住：单击没反应
    tb.ReadOnly = true;
    _cellCanEdit[tb] = canEdit ?? (() => true);
 
    // 编辑结束：失焦自动锁回去
    tb.LostFocus += (s, e) =>
    {
      if (!tb.ReadOnly) EndEditCell(tb);
    };

    // 回车 / Esc：结束编辑并锁回去
    tb.KeyDown += (s, e) =>
    {
      if (e.Key == Keys.Enter || e.Key == Keys.Escape)
      {
        EndEditCell(tb);
        e.Handled = true;
      }
    };

    // 兜底：如果还处于只读状态，获取焦点就立刻丢回面板（避免出现光标/选中）
    tb.GotFocus += (s, e) =>
    {
      if (tb.ReadOnly)
      {
        try { this.Focus(); } catch { }
      }
    };

    // 用 native 层吞单击、放双击（WPF/WinForms 兼容）
    tb.LoadComplete += (s, e) => HookNativeClickBehavior(tb);
    HookNativeClickBehavior(tb);
  }

  void HookNativeClickBehavior(TextBox tb)
 {
  if (tb == null) return;

  object native = null;
  try { native = GetNativeControlObject(tb); } catch { }
  if (native == null) return;

  // 防重复挂钩
  if (_nativeCellMap.ContainsKey(native)) return;

  _nativeCellMap[native] = tb;

  try
  {
    // WPF：PreviewMouseLeftButtonDown（可设置 Handled，真正做到“单击没反应”）
    var eiWpf = native.GetType().GetEvent("PreviewMouseLeftButtonDown");
    if (eiWpf != null)
    {
      var mi = this.GetType().GetMethod(
        nameof(OnWpfCellPreviewMouseLeftButtonDown),
        BindingFlags.Instance | BindingFlags.NonPublic);

      if (mi != null)
      {
        var del = Delegate.CreateDelegate(eiWpf.EventHandlerType, this, mi);
        eiWpf.AddEventHandler(native, del);
        return;
      }
    }

    // WinForms：MouseDown（没有 Handled，但配合 GotFocus 兜底也能达到效果）
    var eiWf = native.GetType().GetEvent("MouseDown");
    if (eiWf != null)
    {
      var mi = this.GetType().GetMethod(
        nameof(OnWinFormsCellMouseDown),
        BindingFlags.Instance | BindingFlags.NonPublic);

      if (mi != null)
      {
        var del = Delegate.CreateDelegate(eiWf.EventHandlerType, this, mi);
        eiWf.AddEventHandler(native, del);
        return;
      }
    }
  }
  catch { }
 }

  // ===== WPF handler：单击吞掉，双击进入编辑 =====
  void OnWpfCellPreviewMouseLeftButtonDown(object sender, object e)
 {
  if (sender == null || e == null) return;
  if (!_nativeCellMap.TryGetValue(sender, out var tb) || tb == null) return;

  // ✅ 单击/双击都先选中整行（否则 Eto.MouseDown 根本不会触发）
  if (_tbRowMap.TryGetValue(tb, out var row) && row != null)
  {
   SelectRow(row);
   SelectClippingPlaneForRow(row); // ✅ 单击行：同步选中该行 clippingplane（如有）
  }


  int clickCount = 1;
  try
  {
    var p = e.GetType().GetProperty("ClickCount");
    if (p != null) clickCount = (int)p.GetValue(e, null);
  }
  catch { }

  // 吞掉事件（保证单击没反应）
  try
  {
    var ph = e.GetType().GetProperty("Handled");
    if (ph != null && ph.CanWrite) ph.SetValue(e, true, null);
  }
  catch { }

  if (clickCount >= 2)
  {
    // 判断是否允许编辑（±0行会返回 false）
    if (_cellCanEdit.TryGetValue(tb, out var canEdit) && canEdit != null && !canEdit())
    {
      try { this.Focus(); } catch { }
      return;
    }

    BeginEditCell(tb);
  }
  else
  {
    // 单击：不做任何事
    try { this.Focus(); } catch { }
  }
 }

  // ===== WinForms handler：双击进入编辑；单击交给 GotFocus 兜底 =====
  void OnWinFormsCellMouseDown(object sender, object e)
 {
  if (sender == null || e == null) return;
  if (!_nativeCellMap.TryGetValue(sender, out var tb) || tb == null) return;
  
  // ✅ 新增：先选中行
  if (_tbRowMap.TryGetValue(tb, out var row) && row != null)
  {
   SelectRow(row);
   SelectClippingPlaneForRow(row); // ✅ 单击行：同步选中该行 clippingplane（如有）
  }


  int clicks = 1;
  try
  {
    var p = e.GetType().GetProperty("Clicks");
    if (p != null) clicks = (int)p.GetValue(e, null);
  }
  catch { }

  if (clicks >= 2)
  {
    if (_cellCanEdit.TryGetValue(tb, out var canEdit) && canEdit != null && !canEdit())
      return;

    BeginEditCell(tb);
  }
  else
  {
    try { this.Focus(); } catch { }
  }
 }


  
void TryMakeButtonFlat(Button btn, Color back, Color border)
{
 if (btn == null) return;

 object native = null;
 try { native = GetNativeControlObject(btn); } catch { native = null; }
 if (native == null) return;

 bool applied = false;

 try
 {
   Type nt = native.GetType();

   // ===== WPF Button（不依赖 Type.GetType，避免部分机器返回 null） =====
   if (IsTypeOrSubclass(nt, "System.Windows.Controls.Button"))
   {
     object backBrush = NewWpfBrush(back);
     object bdBrush   = NewWpfBrush(border);
     object fgBrush   = NewWpfBrush(btn.TextColor);
     object thick1    = NewWpfThickness(1.0);
     object thick0    = NewWpfThickness(0.0);

     if (backBrush != null) SetProp(native, "Background", backBrush);
     if (fgBrush   != null) SetProp(native, "Foreground", fgBrush);
     if (bdBrush   != null) SetProp(native, "BorderBrush", bdBrush);
     if (thick1    != null) SetProp(native, "BorderThickness", thick1);
     if (thick0    != null) SetProp(native, "Padding", thick0);

     SetProp(native, "FocusVisualStyle", null);
     applied = true;
     return;
   }

   // ===== WinForms Button =====
   if (IsTypeOrSubclass(nt, "System.Windows.Forms.Button"))
   {
     // 允许自绘颜色
     SetProp(native, "UseVisualStyleBackColor", false);

     // FlatStyle = Flat
     var flatStyleType = FindLoadedType("System.Windows.Forms.FlatStyle, System.Windows.Forms");
     if (flatStyleType != null)
     {
       object flatVal = null;
       try { flatVal = Enum.Parse(flatStyleType, "Flat"); } catch { flatVal = null; }
       if (flatVal != null) SetProp(native, "FlatStyle", flatVal);
     }

     // BackColor / ForeColor
     SetProp(native, "BackColor", NewDrawingColor(back));
     SetProp(native, "ForeColor", NewDrawingColor(btn.TextColor));

     // FlatAppearance.BorderColor / BorderSize
     var faProp = nt.GetProperty("FlatAppearance",
       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
     var fa = faProp != null ? faProp.GetValue(native, null) : null;
     if (fa != null)
     {
       var faType = fa.GetType();
       var bcProp = faType.GetProperty("BorderColor",
         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
       var bsProp = faType.GetProperty("BorderSize",
         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

       if (bcProp != null && bcProp.CanWrite) bcProp.SetValue(fa, NewDrawingColor(border), null);
       if (bsProp != null && bsProp.CanWrite) bsProp.SetValue(fa, 1, null);
     }

     applied = true;
     return;
   }
 }
 catch { applied = false; }

 // ===== fallback：至少保证可见（避免“白板”）=====
 if (!applied)
 {
   try
   {
     btn.TextColor = Colors.Black;
     btn.BackgroundColor = Colors.White;
   }
   catch { }
 }
}


  void MakeButtonAsEmptyCell(Button btn, Color back)
 {
  if (btn == null) return;

  // ✅ 就一个空格占位（最稳）
  btn.Text  = " ";
  btn.Image = null;

  // ✅ 不要禁用！WPF 禁用会自动变浅/发白（你看到的“白色块”很多就是这个）
  btn.Enabled = true;

  btn.BackgroundColor = back;
  btn.TextColor = back; // 让空格“看不见”

  // ✅ 边框同色=看起来就是空格子
  TryMakeButtonFlat(btn, back, back);
 }




  object GetNativeControlObject(Control c)
  {
    if (c == null) return null;

    var pi = c.GetType().GetProperty("ControlObject",
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (pi != null)
    {
      var obj = pi.GetValue(c, null);
      if (obj != null) return obj;
    }

    var hi = c.GetType().GetProperty("Handler",
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    var handler = (hi != null) ? hi.GetValue(c, null) : null;
    if (handler != null)
    {
      var pci = handler.GetType().GetProperty("Control",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      if (pci != null)
      {
        var obj2 = pci.GetValue(handler, null);
        if (obj2 != null) return obj2;
      }
    }

    return null;
  }

  void RegisterCellTextBox(TextBox tb, PlanLevelRow row, Func<bool> canEdit)
 {
  if (tb == null || row == null) return;

  // 先用 Eto 层保证“至少能选中行”（双击编辑主要靠 native 拦截）
  tb.GotFocus += (s, e) => { try { this.Focus(); } catch { } };

  object native = GetNativeControlObject(tb);
  if (native == null) return;

  _nativeCellRow[native] = row;
  _nativeCanEdit[native] = canEdit ?? (() => true);
  _nativeEtoBox[native]  = tb;

  try
  {
    Type nt = native.GetType();

    // WPF：PreviewMouseLeftButtonDown（可 Handled=true 阻止单击进入编辑）
    var wpfTbType = Type.GetType("System.Windows.Controls.TextBox, PresentationFramework");
    if (wpfTbType != null && wpfTbType.IsAssignableFrom(nt))
    {
      var ev = nt.GetEvent("PreviewMouseLeftButtonDown");
      if (ev != null)
      {
        var mi  = this.GetType().GetMethod("OnNativeCellMouseDown",
          BindingFlags.Instance | BindingFlags.NonPublic);
        var del = Delegate.CreateDelegate(ev.EventHandlerType, this, mi);
        ev.AddEventHandler(native, del);
      }
      return;
    }

    // WinForms：MouseDown（阻止不了焦点，但我们会立刻 this.Focus() 抢回来）
    var wfTbType = Type.GetType("System.Windows.Forms.TextBox, System.Windows.Forms");
    if (wfTbType != null && wfTbType.IsAssignableFrom(nt))
    {
      var ev = nt.GetEvent("MouseDown");
      if (ev != null)
      {
        var mi  = this.GetType().GetMethod("OnNativeCellMouseDown",
          BindingFlags.Instance | BindingFlags.NonPublic);
        var del = Delegate.CreateDelegate(ev.EventHandlerType, this, mi);
        ev.AddEventHandler(native, del);
      }
    }
  }
  catch { }
 }

  // ✅ 单击：只选中行（蓝色覆盖），不进入编辑
  // ✅ 双击：才允许进入编辑（仅对 canEdit=true 的格子）
  void OnNativeCellMouseDown(object sender, EventArgs e)
 {
  if (sender == null || e == null) return;

  if (!_nativeCellRow.TryGetValue(sender, out var row)) return;

  SelectRow(row);

  int clickCount = 1;
  try
  {
    var p = e.GetType().GetProperty("ClickCount");
    if (p != null) clickCount = Convert.ToInt32(p.GetValue(e, null));
  }
  catch { }

  try
  {
    var p = e.GetType().GetProperty("Clicks");
    if (p != null) clickCount = Convert.ToInt32(p.GetValue(e, null));
  }
  catch { }

  bool canEdit = true;
  try
  {
    if (_nativeCanEdit.TryGetValue(sender, out var f) && f != null)
      canEdit = f();
  }
  catch { canEdit = true; }

  // 双击且允许编辑：放行
  if (clickCount >= 2 && canEdit)
  {
    try
    {
      if (_nativeEtoBox.TryGetValue(sender, out var tb) && tb != null)
        tb.Focus();
    }
    catch { }
    return;
  }

  // 单击 or 不允许编辑：阻止获得焦点/光标（WPF 有 Handled）
  try
  {
    var handledProp = e.GetType().GetProperty("Handled");
    if (handledProp != null && handledProp.CanWrite)
      handledProp.SetValue(e, true, null);
  }
  catch { }

  try { this.Focus(); } catch { }
 }


  void SetProp(object obj, string propName, object val)
  {
    if (obj == null || string.IsNullOrEmpty(propName)) return;
    var p = obj.GetType().GetProperty(propName,
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (p != null && p.CanWrite)
      p.SetValue(obj, val, null);
  }

  
Type FindLoadedType(string typeNameOrAsmQualified)
{
  if (string.IsNullOrWhiteSpace(typeNameOrAsmQualified)) return null;

  // 允许传 "FullName, Assembly" 或仅 "FullName"
  string nameOnly = typeNameOrAsmQualified;
  int comma = typeNameOrAsmQualified.IndexOf(',');
  if (comma > 0) nameOnly = typeNameOrAsmQualified.Substring(0, comma).Trim();

  // 1) 直接 Type.GetType（对带 Assembly 的最有效）
  try
  {
    var t0 = Type.GetType(typeNameOrAsmQualified, false);
    if (t0 != null) return t0;
  }
  catch { }

  // 2) 扫描已加载程序集（更稳，避免某些机器 Type.GetType 返回 null）
  try
  {
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
      if (asm == null) continue;
      try
      {
        var t = asm.GetType(nameOnly, false);
        if (t != null) return t;
      }
      catch { }
    }
  }
  catch { }

  return null;
}

bool IsTypeOrSubclass(Type t, string fullName)
{
  if (t == null || string.IsNullOrEmpty(fullName)) return false;
  try
  {
    for (Type cur = t; cur != null; cur = cur.BaseType)
    {
      if (string.Equals(cur.FullName, fullName, StringComparison.Ordinal)) return true;
    }
  }
  catch { }
  return false;
}


object NewWpfThickness(double uniform)
{
  var t = FindLoadedType("System.Windows.Thickness, PresentationFramework");
  if (t == null) return null;
  try { return Activator.CreateInstance(t, new object[] { uniform }); }
  catch { return null; }
}


  
object NewWpfBrush(Color c)
{
  var colorType = FindLoadedType("System.Windows.Media.Color, PresentationCore");
  if (colorType == null) return null;

  var mi = colorType.GetMethod("FromArgb",
    BindingFlags.Public | BindingFlags.Static,
    null,
    new Type[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) },
    null);
  if (mi == null) return null;

  byte a = ToByte(c.A);
  byte r = ToByte(c.R);
  byte g = ToByte(c.G);
  byte b = ToByte(c.B);

  object wpfColor = null;
  try { wpfColor = mi.Invoke(null, new object[] { a, r, g, b }); }
  catch { wpfColor = null; }
  if (wpfColor == null) return null;

  var brushType = FindLoadedType("System.Windows.Media.SolidColorBrush, PresentationCore");
  if (brushType == null) return null;

  try { return Activator.CreateInstance(brushType, new object[] { wpfColor }); }
  catch { return null; }
}


  byte ToByte(float v01)
  {
    double v = v01;
    if (v < 0) v = 0;
    if (v > 1) v = 1;
    return (byte)Math.Round(v * 255.0);
  }

  
object NewDrawingColor(Color c)
{
  var t = FindLoadedType("System.Drawing.Color, System.Drawing");
  if (t == null) return null;

  var mi = t.GetMethod("FromArgb",
    BindingFlags.Public | BindingFlags.Static,
    null,
    new Type[] { typeof(int), typeof(int), typeof(int), typeof(int) },
    null);
  if (mi == null) return null;

  int a = (int)ToByte(c.A);
  int r = (int)ToByte(c.R);
  int g = (int)ToByte(c.G);
  int b = (int)ToByte(c.B);

  try { return mi.Invoke(null, new object[] { a, r, g, b }); }
  catch { return null; }
}


  void HookSelectRow(Control c, PlanLevelRow row)
  {
   if (c == null || row == null) return;

   // 单击即选中整行；右键弹出“标高行菜单”
   c.MouseDown += (s, e) =>
   {
     bool isRight = IsRightMouseDown(e);

     // ✅ 右键：先选中行（像图层面板一样），再弹出菜单
     if (isRight)
     {
       SelectRow(row);

       // ✅ 右键菜单：不要在 Show() 之后抢焦点，否则菜单可能闪退
       var pt = GetMousePoint(e);
       TrySetMouseHandled(e);

       try
       {
         Application.Instance.AsyncInvoke(() => ShowRowContextMenu(row, c, pt));
       }
       catch
       {
         ShowRowContextMenu(row, c, pt);
       }

       return;
     }

     // 左键：保持原行为
     SelectRow(row);
     SelectClippingPlaneForRow(row);

     // 你之前要求“单击输入框没反应”（不出现光标/不进入编辑）
     // 这里把焦点抢回面板，避免 TextBox 出现插入光标
     try { this.Focus(); } catch { }
   };
  }

  // ===================== 行右键菜单（第一步：只放一个入口） =====================
  void ShowRowContextMenu(PlanLevelRow row, Control host, Eto.Drawing.Point pt)
  {
    if (row == null || host == null) return;

    ContextMenu menu = BuildRowContextMenu(row);
    if (menu == null) return;

    // 保持一个引用，避免某些环境下菜单闪退（被过早 GC/Dispose）
    try { if (_activeRowContextMenu != null) _activeRowContextMenu.Dispose(); } catch { }
    _activeRowContextMenu = menu;
    try
    {
      _activeRowContextMenu.Closed += (s, e) =>
      {
        try { if (ReferenceEquals(_activeRowContextMenu, menu)) _activeRowContextMenu = null; } catch { }
      };
    }
    catch { }

    // 尽量在鼠标处弹出（不同 Eto 版本 Show() 重载可能不同，这里用反射兜底）
    try
    {
      var mi2 = typeof(ContextMenu).GetMethod("Show", new Type[] { typeof(Control), typeof(Eto.Drawing.Point) });
      if (mi2 != null)
      {
        mi2.Invoke(menu, new object[] { host, pt });
        return;
      }

      var mi1 = typeof(ContextMenu).GetMethod("Show", new Type[] { typeof(Control) });
      if (mi1 != null)
      {
        mi1.Invoke(menu, new object[] { host });
        return;
      }
    }
    catch { }
  }


  ContextMenu BuildRowContextMenu(PlanLevelRow row)
  {
    var menu = new ContextMenu();
    // ✅ 1.4.5：复制标高层（与顶部 CO 同功能）
    var miCopyLevel = new ButtonMenuItem { Text = "复制标高层" };
    miCopyLevel.Click += (s, e) =>
    {
      try
      {
        // 右键已选中；这里再保险一次
        SelectRow(row);
        CopySelectedRow();
      }
      catch
      {
        try { RhinoApp.WriteLine("[PlanGen] 右键菜单：复制标高层失败。"); } catch { }
      }
    };
    menu.Items.Add(miCopyLevel);



    // ✅ 第一步：只先放一个功能入口（先把菜单跑通）
    var miShowRelated = new ButtonMenuItem { Text = "显示与本层有关的模型" };
    miShowRelated.Click += (s, e) =>
    {
      try
      {
        SelectModelsRelatedToRow(row);
      }
      catch
      {
        try { RhinoApp.WriteLine("[PlanGen] 右键菜单：选择相关模型失败。"); } catch { }
      }
    };

    menu.Items.Add(miShowRelated);
    return menu;
  }

  // ✅ 右键菜单功能：按本行“模式（图层/模型/不限）”全选相关模型（过滤曲线/点）
  void SelectModelsRelatedToRow(PlanLevelRow row)
  {
    if (row == null)
    {
      try { Rhino.UI.Dialogs.ShowMessageBox("请先选择模式", "提示"); } catch { }
      return;
    }

    // ✅ 未选择模式/未绑定完成：弹窗提示
    if (!IsRowModeReadyForPlot(row))
    {
      try { Rhino.UI.Dialogs.ShowMessageBox("请先选择模式", "提示"); } catch { }
      return;
    }

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
    {
      try { Rhino.UI.Dialogs.ShowMessageBox("未能读取当前文档。", "提示"); } catch { }
      return;
    }

    // ✅ 过滤：只选“模型对象”，不选曲线/点/注释/剖切面等
    ObjectType modelFilter = ObjectType.Brep
                           | ObjectType.Surface
                           | ObjectType.Extrusion
                           | ObjectType.Mesh
                           | ObjectType.SubD
                           | ObjectType.InstanceReference;

    // 复用落图的“按模式收集对象”逻辑，保证一致
    List<RhinoObject> cand = null;
    try { cand = CollectRowPlotTargets(doc, row, Guid.Empty); } catch { cand = null; }
    if (cand == null) cand = new List<RhinoObject>();

    // 清空当前选择
    try { doc.Objects.UnselectAll(true); } catch { }

    int sel = 0;
    foreach (var ro in cand)
    {
      if (ro == null) continue;
      if (ro.Id == Guid.Empty) continue;

      // ✅ 过滤曲线/点：只允许 modelFilter
      try
      {
        if ((ro.ObjectType & modelFilter) == 0) continue;
      }
      catch { continue; }

      bool ok = false;
      try
      {
        // RhinoObject.Select 更稳定一些（会走对象自身状态）
        ro.Select(true);
      }
      catch
      {
        try { doc.Objects.Select(ro.Id, true); } catch { }
      }

      ok = IsRhinoObjectSelected(ro);
      // 若无法判断（某些版本/对象类型），保守认为已选中以免误报 0
      // IsRhinoObjectSelected 内部已做了兜底

      if (ok) sel++;

}

    try { doc.Views.Redraw(); } catch { }

    if (sel <= 0)
    {
      try { RhinoApp.WriteLine("[PlanGen] 未找到可选的模型对象（已过滤曲线/点）。"); } catch { }
    }
    else
    {
      try { RhinoApp.WriteLine("[PlanGen] 已选中与当前标高行有关的模型：{0} 个。", sel); } catch { }
    }
  }

  void TrySetMouseHandled(MouseEventArgs e)
  {
    if (e == null) return;
    try
    {
      var pHandled = e.GetType().GetProperty("Handled");
      if (pHandled != null && pHandled.CanWrite)
        pHandled.SetValue(e, true, null);
    }
    catch { }
  }

  // ✅ 兼容不同 RhinoCommon 版本：IsSelected(bool) 可能返回 bool 或 int
  bool IsRhinoObjectSelected(RhinoObject ro)
  {
    if (ro == null) return false;

    try
    {
      // 1) 反射调用 IsSelected(bool)
      var mi = ro.GetType().GetMethod("IsSelected", new Type[] { typeof(bool) });
      if (mi != null)
      {
        object ret = mi.Invoke(ro, new object[] { false });
        if (ret is bool) return (bool)ret;
        if (ret is int) return ((int)ret) != 0;
      }
    }
    catch { }

    try
    {
      // 2) 反射调用 IsSelected()（无参版本）
      var mi0 = ro.GetType().GetMethod("IsSelected", Type.EmptyTypes);
      if (mi0 != null)
      {
        object ret = mi0.Invoke(ro, null);
        if (ret is bool) return (bool)ret;
        if (ret is int) return ((int)ret) != 0;
      }
    }
    catch { }

    // 无法判断时：保守返回 true，避免“实际已选中但计数为 0”导致误导
    return true;
  }

  bool IsRightMouseDown(MouseEventArgs e)

  {
    if (e == null) return false;
    try
    {
      // Eto 常见：Buttons / Button（不同版本差异）
      var pButtons = e.GetType().GetProperty("Buttons");
      if (pButtons != null)
      {
        var v = pButtons.GetValue(e, null);
        if (v != null)
        {
          string s = v.ToString() ?? "";
          if (s.IndexOf("Alternate", StringComparison.OrdinalIgnoreCase) >= 0) return true;
          if (s.IndexOf("Secondary", StringComparison.OrdinalIgnoreCase) >= 0) return true;
          if (s.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
      }

      var pButton = e.GetType().GetProperty("Button");
      if (pButton != null)
      {
        var v = pButton.GetValue(e, null);
        if (v != null)
        {
          string s = v.ToString() ?? "";
          if (s.IndexOf("Alternate", StringComparison.OrdinalIgnoreCase) >= 0) return true;
          if (s.IndexOf("Secondary", StringComparison.OrdinalIgnoreCase) >= 0) return true;
          if (s.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
      }
    }
    catch { }
    return false;
  }

  Eto.Drawing.Point GetMousePoint(MouseEventArgs e)
  {
    try
    {
      var pLoc = e.GetType().GetProperty("Location");
      if (pLoc != null)
      {
        var loc = pLoc.GetValue(e, null);
        if (loc != null)
        {
          double x = 0.0, y = 0.0;
          var px = loc.GetType().GetProperty("X");
          var py = loc.GetType().GetProperty("Y");
          if (px != null) x = Convert.ToDouble(px.GetValue(loc, null));
          if (py != null) y = Convert.ToDouble(py.GetValue(loc, null));
          return new Eto.Drawing.Point((int)Math.Round(x), (int)Math.Round(y));
        }
      }
    }
    catch { }
    return new Eto.Drawing.Point(0, 0);
  }

  void BindCellToRow(TextBox tb, PlanLevelRow row)
  {
   if (tb == null || row == null) return;
   _tbRowMap[tb] = row;
  }

  void ClearKeyboardFocusNative()
  {
   try
   {
    // WPF: System.Windows.Input.Keyboard.ClearFocus()
    var kbType = Type.GetType("System.Windows.Input.Keyboard, PresentationCore");
    var mi = kbType?.GetMethod("ClearFocus", BindingFlags.Public | BindingFlags.Static);
    if (mi != null) mi.Invoke(null, null);
   }
   catch { }
  }
}

// ===================== 显示函数（不依赖 RunScript 签名） =====================
void ShowPlanGenerationPanel()
{
  // ✅ 强制让窗口“跟着 Rhino 主窗走”，避免出现：在任务栏有缩略图但点不出来/飞窗/最小化卡住
  RhinoApp.InvokeOnUiThread((Action)(() =>
  {
    RhinoApp.WriteLine("[PlanGen] ShowPlanGenerationPanel called.");


    // ★ 提供给 PlanGen 面板：落图后调用 Wall2D 的“刷新 + 重绘（仅新轴线）”
    try
    {
      if (PlanGenState.Wall2D_PostPlot_RefreshThenRedraw == null)
      {
        PlanGenState.Wall2D_PostPlot_RefreshThenRedraw = (RhinoDoc d, int axisLi, List<Guid> axisIds) =>
        {
          if (d == null) return;
          if (axisLi < 0 || axisLi >= d.Layers.Count) return;

          int oldCur = -1;
          try { oldCur = d.Layers.CurrentLayerIndex; } catch { oldCur = -1; }

          // 1) 临时把 PB-Wall-Axis 图层设为当前激活层（Wall2D 以此决定 parent 分区/F6）
          try { d.Layers.SetCurrentLayerIndex(axisLi, true); } catch { }

          // 2) 先 Refresh：清理被 PlanGen 删除的墙轴线所对应的墙线/填充等
          try { RunRefreshForCurrentParent(d, null); } catch { }

          // 3) 再 Redraw（仅本轮新轴线）
//    ✅ 后台模式：若本轮没有新轴线，则直接跳过，不进入任何交互提示
          if (axisIds != null && axisIds.Count > 0)
          {
            try
            {
              RunRedrawWallsForSelection(d, null, axisIds, true);
            }
            catch { }
          }

          // restore current layer
          try
          {
            if (oldCur >= 0 && oldCur < d.Layers.Count)
              d.Layers.SetCurrentLayerIndex(oldCur, true);
          }
          catch { }
        };
      }
    }
    catch { }


    Eto.Drawing.Point CenterOnPrimary(Eto.Drawing.Size sz)
    {
      try
      {
        var sc = Eto.Forms.Screen.PrimaryScreen;
        if (sc != null)
        {
          // WorkingArea 在不同 Eto 版本可能是 Rectangle / RectangleF，这里统一按 float 取值再 cast
          float wx = 0, wy = 0, ww = 0, wh = 0;
          try
          {
            object waObj = sc.WorkingArea;
            // RectangleF
            if (waObj is Eto.Drawing.RectangleF waf)
            { wx = waf.X; wy = waf.Y; ww = waf.Width; wh = waf.Height; }
            else if (waObj is Eto.Drawing.Rectangle war)
            { wx = war.X; wy = war.Y; ww = war.Width; wh = war.Height; }
            else
            {
              // 反射兜底
              var t = waObj.GetType();
              wx = Convert.ToSingle(t.GetProperty("X")?.GetValue(waObj) ?? 0f);
              wy = Convert.ToSingle(t.GetProperty("Y")?.GetValue(waObj) ?? 0f);
              ww = Convert.ToSingle(t.GetProperty("Width")?.GetValue(waObj) ?? 0f);
              wh = Convert.ToSingle(t.GetProperty("Height")?.GetValue(waObj) ?? 0f);
            }
          }
          catch { }

          int x = (int)(wx + Math.Max(0, (ww - sz.Width) / 2.0f));
          int y = (int)(wy + Math.Max(0, (wh - sz.Height) / 2.0f));
          return new Eto.Drawing.Point(x, y);
        }
      }
      catch { }
      return new Eto.Drawing.Point(80, 80);
    }

    void ForceActivate(Eto.Forms.Form f)
    {
      if (f == null) return;

      // 1) 尽量设为 Rhino 主窗的 owned window（否则容易出现“点任务栏不弹出”）
      try { f.Owner = Rhino.UI.RhinoEtoApp.MainWindow; } catch { }

      // 2) 不在任务栏单独占位（跟着 Rhino）
      try { f.ShowInTaskbar = false; } catch { }

      // 3) 强制恢复/置前/激活
      try { f.WindowState = Eto.Forms.WindowState.Normal; } catch { }

      // 某些环境 BringToFront/Focus 不够，用 Activate + Topmost 抖动更稳（反射避免版本差异）
      try { f.Show(); } catch { }
      try { f.Visible = true; } catch { }

      try { f.Location = CenterOnPrimary(f.ClientSize); } catch { }

      try { f.BringToFront(); } catch { }
      try { f.Focus(); } catch { }
      try
      {
        var miAct = f.GetType().GetMethod("Activate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (miAct != null) miAct.Invoke(f, null);
      }
      catch { }

      // Topmost 抖动（如果属性存在）
      try
      {
        var pi = f.GetType().GetProperty("Topmost");
        if (pi != null && pi.PropertyType == typeof(bool) && pi.CanWrite)
        {
          pi.SetValue(f, true);
          pi.SetValue(f, false);
        }
      }
      catch { }

      // 让 Rhino 主窗也拿到焦点（反射避免 API 差异）
      try
      {
        var mi = typeof(RhinoApp).GetMethod("SetFocusToMainWindow", BindingFlags.Public | BindingFlags.Static);
        if (mi != null) mi.Invoke(null, null);
      }
      catch { }
    }

    // 已有面板：直接强制激活
    if (PlanGenState.Panel != null)
    {
      try
      {
        ForceActivate(PlanGenState.Panel);
        return;
      }
      catch
      {
        PlanGenState.Panel = null;
      }
    }

    // 新建 + 显示（全部在 UI 线程）
    try
    {
      var p = new PlanGenerationPanel();
      PlanGenState.Panel = p;

      ForceActivate(p);
    }
    catch (Exception ex)
    {
      PlanGenState.Panel = null;
      RhinoApp.WriteLine("[PlanGen] Create/Show failed: " + ex.ToString());
    }
  }));
}


// ===================== 顶层入口：只要点Run就一定执行 =====================
void Main()
{
  ShowPlanGenerationPanel();
}



// ===================================================================================
// ===================== Wall2D-1.3.3 merged in (panel entry removed) =================
// ===================================================================================

// ==================== 0. 公共状态 & 数据结构 ====================

static class RTZ3State
{
  public static bool HasLastLocation = false;
  public static Eto.Drawing.Point LastLocation;

  public static bool   HasLastPanelState        = false;

  public static double LastLeftThickness        = 100.0;
  public static double LastRightThickness       = 100.0;

  public static double LastInsulLeftOffset      = 0.0;
  public static double LastInsulRightOffset     = 0.0;

  public static bool   LastLinkChecked          = true;
  public static bool   LastInsulLeftChecked     = false;
  public static bool   LastInsulRightChecked    = false;
  public static bool   LastUseInsulOutlineLeft  = false;
  public static bool   LastUseInsulOutlineRight = false;

   // ★ 新增：上一次使用的墙类型
  public static WallKind LastWallKind = WallKind.Concrete;
  public static AxisDrawMode  LastAxisMode  = AxisDrawMode.Line;   // ★ 新增：记住上次是直墙还是弧墙
    
    // ★ 单线变墙：是否已经初始化过默认参数
  public static bool HasUsedSingleToWallOnce = false;
    // ★★★ 新增：统一存储“打断之后、待处理的轴线 Id”
  public static List<Guid> axistobedeal = new List<Guid>();

  public static bool SuppressCapsOnce = false;
  
}


// ==================== 0.x  Name 额外 token 继承缓存（1.3.3） ====================
// 目的：在“删旧建新”时，把 Name 中非本脚本维护的字段（如 GeoId=...）原样继承到新对象；
// 同时避免把本脚本维护的字段（AxisId/Start/End/...）原封不动带过去造成冲突。
static class NameCarryState
{
  // Key：旧 AxisId
  public static Dictionary<Guid, List<string>> AxisExtras = new Dictionary<Guid, List<string>>();

  // Key："{oldAxisId:D}|{Prefix}|{Side}"，例如 "....|Wall|Left"
  public static Dictionary<string, List<string>> CurveExtras = new Dictionary<string, List<string>>();

  // ===== owned key sets（按前缀区分）=====
  static HashSet<string> _ownedWallaxisKeys;
  static HashSet<string> _ownedWallKeys;
  static HashSet<string> _ownedWallinsideKeys;
  static HashSet<string> _ownedInsulationKeys;

  static HashSet<string> OwnedWallaxisKeys
  {
    get
    {
      if (_ownedWallaxisKeys == null)
      {
        _ownedWallaxisKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
          "AxisId","Axistype","L","R","Walltype",
          "InsulL","InsulR","InsulLOn","InsulROn",
          "UseInsulLAsOutline","UseInsulRAsOutline",
          "Start","End"
        };
      }
      return _ownedWallaxisKeys;
    }
  }

  static HashSet<string> OwnedWallKeys
  {
    get
    {
      if (_ownedWallKeys == null)
      {
        _ownedWallKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
          "AxisId","Side","Walltype","AxisStart","AxisEnd","Start","End"
        };
      }
      return _ownedWallKeys;
    }
  }

  static HashSet<string> OwnedWallinsideKeys
  {
    get
    {
      if (_ownedWallinsideKeys == null)
      {
        _ownedWallinsideKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
          "AxisId","Side","Walltype","AxisStart","AxisEnd","Start","End"
        };
      }
      return _ownedWallinsideKeys;
    }
  }

  static HashSet<string> OwnedInsulationKeys
  {
    get
    {
      if (_ownedInsulationKeys == null)
      {
        _ownedInsulationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
          "AxisId","Side","Offset","Start","End"
        };
      }
      return _ownedInsulationKeys;
    }
  }

  static string MakeCurveKey(Guid oldAxisId, string prefix, string side)
  {
    string p = (prefix ?? "").Trim();
    if (string.IsNullOrWhiteSpace(p)) p = "Curve";

    string s = (side ?? "").Trim();
    if (string.IsNullOrWhiteSpace(s)) s = "";

    return oldAxisId.ToString("D") + "|" + p + "|" + s;
  }

  static string GetPrefix(string name)
  {
    if (string.IsNullOrEmpty(name)) return "";
    int i = name.IndexOf('|');
    if (i < 0) return name.Trim();
    return name.Substring(0, i).Trim();
  }

  static bool TryGetTagValue(string name, string key, out string value)
  {
    value = null;
    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key)) return false;

    string[] parts = name.Split('|');
    for (int i = 0; i < parts.Length; i++)
    {
      string t = (parts[i] ?? "").Trim();
      if (string.IsNullOrEmpty(t)) continue;

      int eq = t.IndexOf('=');
      if (eq <= 0) continue;

      string k = t.Substring(0, eq).Trim();
      if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

      value = (eq < t.Length - 1) ? t.Substring(eq + 1).Trim() : "";
      return true;
    }
    return false;
  }

  static List<string> ExtractExtraTokens(string fullName, HashSet<string> ownedKeys)
  {
    var res = new List<string>();
    if (string.IsNullOrEmpty(fullName)) return res;

    string[] parts = fullName.Split('|');
    if (parts == null || parts.Length == 0) return res;

    // parts[0] 是前缀（Wallaxis/Wall/Insulation/...），不参与
    for (int i = 1; i < parts.Length; i++)
    {
      string token = (parts[i] ?? "").Trim();
      if (string.IsNullOrEmpty(token)) continue;

      int eq = token.IndexOf('=');
      if (eq <= 0)
      {
        // 兼容极少数“无等号 token”（默认也作为额外 token 继承）
        res.Add(token);
        continue;
      }

      string k = token.Substring(0, eq).Trim();
      if (ownedKeys != null && ownedKeys.Contains(k))
        continue;

      res.Add(token);
    }
    return res;
  }

  public static void StoreAxisExtras(Guid oldAxisId, string axisName)
  {
    if (oldAxisId == Guid.Empty) return;

    // 刷新覆盖（避免上一轮残留）
    if (AxisExtras.ContainsKey(oldAxisId))
      AxisExtras.Remove(oldAxisId);

    var extras = ExtractExtraTokens(axisName, OwnedWallaxisKeys);
    AxisExtras[oldAxisId] = extras;
  }

  public static void StoreCurveExtras(Guid oldAxisId, string curveName)
  {
    if (oldAxisId == Guid.Empty) return;
    if (string.IsNullOrEmpty(curveName)) return;

    string prefix = GetPrefix(curveName);

    string side = "";
    string tmp;
    if (TryGetTagValue(curveName, "Side", out tmp))
      side = tmp;

    HashSet<string> owned = null;
    if (prefix.Equals("Wall", StringComparison.OrdinalIgnoreCase)) owned = OwnedWallKeys;
    else if (prefix.Equals("Wallinside", StringComparison.OrdinalIgnoreCase)) owned = OwnedWallinsideKeys;
    else if (prefix.Equals("Insulation", StringComparison.OrdinalIgnoreCase)) owned = OwnedInsulationKeys;
    else owned = null; // 未知前缀：不过滤 owned keys（尽量保留）

    string key = MakeCurveKey(oldAxisId, prefix, side);

    // 刷新覆盖（避免上一轮残留）
    if (CurveExtras.ContainsKey(key))
      CurveExtras.Remove(key);

    var extras = ExtractExtraTokens(curveName, owned);
    CurveExtras[key] = extras;
  }

  public static List<string> GetAxisExtras(Guid oldAxisId)
  {
    List<string> v;
    if (oldAxisId != Guid.Empty && AxisExtras.TryGetValue(oldAxisId, out v))
      return v;
    return null;
  }

  public static List<string> GetCurveExtras(Guid oldAxisId, string prefix, string side)
  {
    if (oldAxisId == Guid.Empty) return null;
    string key = MakeCurveKey(oldAxisId, prefix, side);
    List<string> v;
    if (CurveExtras.TryGetValue(key, out v))
      return v;
    return null;
  }

  public static string AppendExtraTokens(string baseName, List<string> extras)
  {
    if (extras == null || extras.Count == 0)
      return baseName ?? "";

    string name = baseName ?? "";
    var parts = new List<string>(name.Split('|'));

    // 收集现有 key，避免重复
    var existKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < parts.Count; i++)
    {
      string t = (parts[i] ?? "").Trim();
      if (string.IsNullOrEmpty(t)) continue;
      int eq = t.IndexOf('=');
      if (eq <= 0) continue;

      string k = t.Substring(0, eq).Trim();
      if (!string.IsNullOrEmpty(k))
        existKeys.Add(k);
    }

    foreach (var tok in extras)
    {
      string token = (tok ?? "").Trim();
      if (string.IsNullOrEmpty(token)) continue;

      int eq = token.IndexOf('=');
      if (eq > 0)
      {
        string k = token.Substring(0, eq).Trim();
        if (!string.IsNullOrEmpty(k) && existKeys.Contains(k))
          continue;
        if (!string.IsNullOrEmpty(k))
          existKeys.Add(k);
      }

      parts.Add(token);
    }

    return string.Join("|", parts);
  }
}


// ===== 数据结构 =====
class AxisInfo
{
  public Curve AxisCurve;
  public Point3d Start;
  public Point3d End;
  public double OffsetL;
  public double OffsetR;

  public WallKind WallType;   // 墙类型
  public Curve WallLeft;      // 左墙线
  public Curve WallRight;     // 右墙线


  // ★ 1.1.2：仅玻璃幕墙使用：内墙线（与 Wall 同层），名称前缀为 Wallinside
  public Curve WallinsideLeft;   // 左内墙线
  public Curve WallinsideRight;  // 右内墙线

  // 轴线曲线类型（Line / Arc / Spline）
  public AxisCurveKind AxisType;

  // 3.0.4 保温 & 外轮廓
  public double InsulLeftOffset;
  public double InsulRightOffset;
  public bool   InsulLeftOn;
  public bool   InsulRightOn;
  public bool   UseInsulLeftAsOutline;
  public bool   UseInsulRightAsOutline;

  public Curve InsulLeftCurve;
  public Curve InsulRightCurve;

  // ★ 新增：这条轴线在文档里的 Guid，用来给墙线 Name 里写 AxisId
  public Guid AxisId;

  // ★ 1.3.3：用于 Name 额外 token 继承——记录本段轴线来源的旧 AxisId
  public Guid SourceAxisId;
}

// ==== 单线变墙：端点信息结构，用于 SmartJoin ====
class JoinEndpointInfo
{
  public Curve   Curve;   // 这条端点属于哪条曲线
  public double  T;       // 对应的参数
  public Point3d Point;   // 端点坐标
}

// 单线变墙用：记录一条“候选轴线段”的端点信息
class SingleAxisEndRef
{
  public int Index;       // 在 segments 列表中的下标
  public Curve Curve;     // 该端点所在的曲线
  public double Parameter;// 端点在曲线上的参数
  public Point3d Point;   // 端点坐标
}

// ==== 判断在一个“共享端点”处，两条曲线是否曲率连续（G1 及近似）====
bool IsCurvatureContinuousAtSharedPoint(
  JoinEndpointInfo e0,
  JoinEndpointInfo e1,
  double tol)
{
  if (e0 == null || e1 == null) return false;
  if (e0.Curve == null || e1.Curve == null) return false;

  var c0 = e0.Curve;
  var c1 = e1.Curve;
  double t0 = e0.T;
  double t1 = e1.T;

  // 曲率向量
  Vector3d k0 = c0.CurvatureAt(t0);
  Vector3d k1 = c1.CurvatureAt(t1);

  double len0 = k0.Length;
  double len1 = k1.Length;

  // 这个阈值用来判断“近似直线”
  double epsK = 1e-6;
  // 角度容差（2°）
  double angleTol = RhinoMath.ToRadians(2.0);

  // —— 情况1：两条都几乎是直线 —— //
  if (len0 < epsK && len1 < epsK)
  {
    Vector3d v0 = c0.TangentAt(t0);
    Vector3d v1 = c1.TangentAt(t1);
    if (!v0.Unitize() || !v1.Unitize())
      return false;

    double angle = Vector3d.VectorAngle(v0, v1);
    // 既允许“同向”，也允许“反向”（两种都算共线）
    if (angle > angleTol && Math.Abs(angle - Math.PI) > angleTol)
      return false;

    return true;
  }

  // —— 情况2：一条接近直线，另一条明显弯曲 —— //
  if ((len0 < epsK && len1 >= epsK) || (len1 < epsK && len0 >= epsK))
    return false;

  // —— 情况3：两条都是弯曲线 —— //
  // 先看曲率模长是否接近（同一圆弧 / 样条的一部分）
  double ratio = Math.Abs(len0 - len1) / Math.Max(len0, len1);
  if (ratio > 0.05) // 5% 以内认为是“同一条几何曲线”
    return false;

  if (!k0.Unitize() || !k1.Unitize())
    return false;

  double angle2 = Vector3d.VectorAngle(k0, k1);
  if (angle2 > angleTol && Math.Abs(angle2 - Math.PI) > angleTol)
    return false;

  return true;
}

// 绘制模式：直线 / 圆弧（三点式）
enum AxisDrawMode
{
  Line,
  Arc
}

// 轴线曲线类型
enum AxisCurveKind
{
  Line,
  Arc,
  Spline   // 预留给 InterpCrv 轴线
}

// 墙种类
enum WallKind
{
  Concrete, // 钢筋砼墙
  Block,    // 填充墙
  Glass,    // 玻璃幕墙
  Stud      // 轻质隔墙
}

// 墙生成操作模式
enum WallGenerateMode
{
  None,
  Generate,      // 绘制墙体
  Refresh,       // 刷新
  SingleToWall,  // 单线变墙
  Edit,          // 编辑墙体
  FormatBrush,   // 格式刷
  ToggleCaps,    // 开关封口
  RedrawWalls,   // 重绘墙体
  RedrawAll      // 全部重绘
}



// ========================================================
// ===============  模块01 轴线输入模块  ===================
// =============== 用户通过不同命令输入轴线 =================

void WallAxisInputOnly()
{
  RhinoDoc doc = RhinoDoc.ActiveDoc;
  if (doc == null) return;

  // 1. 创建墙厚度控制面板（modeless，不阻塞 Rhino）
  var thicknessPanel = new ThicknessPanel(100.0, 100.0);
  var mainWindow = Rhino.UI.RhinoEtoApp.MainWindow;
  thicknessPanel.Owner = mainWindow;

  // 初始位置
  if (RTZ3State.HasLastLocation)
  {
    thicknessPanel.Location = RTZ3State.LastLocation;
  }
  else if (mainWindow != null)
  {
    var bounds = mainWindow.Bounds;
    int x = bounds.X + bounds.Width / 15;
    int y = bounds.Y + bounds.Height / 5;
    thicknessPanel.Location = new Eto.Drawing.Point(x, y);
  }

  thicknessPanel.Show();

  // ★ 不再自动启动 “选择墙轴线第一个点”
  // ★ 只有当用户点击“绘制墙体”按钮时，才真正进入轴线绘制流程
  thicknessPanel.GenerateWallsRequested += () =>
  {
    RunWallAxisDrawSession(doc, thicknessPanel);
  };
  // ★ 新增：单线变墙
  thicknessPanel.SingleToWallRequested += () =>
  {
    RunSingleToWallMode(doc, thicknessPanel);
  };
  // ★ 新增：刷新
    thicknessPanel.RefreshRequested += () =>
  {
    RunRefreshForCurrentParent(doc, thicknessPanel);
  };
   // ★ 新增：重绘墙体（仅选中轴线）
   thicknessPanel.RedrawWallsRequested += () =>
  {
   RunRedrawWallsForSelection(doc, thicknessPanel);
  };

  // ★ 新增：全部重绘（当前分区全量）
    thicknessPanel.RedrawAllRequested += () =>
 {
    RunRedrawAllForCurrentParent(doc, thicknessPanel); // 你之后实现
 };
  // ★ 新增：编辑墙体
    thicknessPanel.EditWallsRequested += () =>
 {
    RunEditWallsMode(doc, thicknessPanel);
 };
    thicknessPanel.FormatBrushRequested += () =>
 {
  RunFormatBrushMode(doc, thicknessPanel);
 };

}

void RunWallAxisDrawSession(RhinoDoc doc, ThicknessPanel thicknessPanel)
{
  if (doc == null) return;
  if (thicknessPanel == null) return;
  if (thicknessPanel.Cancelled) return;

  thicknessPanel.IsDrawingWalls = true;   // ★ 进入绘制状态
  
 try
  {
  double tol = doc.ModelAbsoluteTolerance;

  // 预览颜色（Rhino 视图里）
  SDColor axisPreviewColor       = SDColor.FromArgb(96, 56, 0);       // 轴线棕色
  SDColor leftPreviewColor       = SDColor.FromArgb(201, 255, 155);   // 左侧墙：绿
  SDColor rightPreviewColor      = SDColor.FromArgb(255, 144, 173);   // 右侧墙：粉
  SDColor insulLeftPreviewColor  = SDColor.FromArgb(145, 170, 105);   // 左保温：更暗一点的绿
  SDColor insulRightPreviewColor = SDColor.FromArgb(180, 105, 125);   // 右保温：更暗一点的粉

  // 模式 & 当前墙类型在整次命令中保持记忆
  AxisDrawMode mode = AxisDrawMode.Line;     // 默认直线模式
  mode = thicknessPanel.CurrentShapeMode;    // 和面板当前直 / 弧保持一致

  // 初始墙种类从面板读取
  WallKind currentWallType = thicknessPanel.CurrentWallKind;

  // 当用户在面板里点“直墙 / 弧墙”时，更新 mode
  thicknessPanel.ShapeModeChanged += (shapeMode) =>
  {
    mode = shapeMode;
  };

  bool anyCommitted = false; // 是否已经有过至少一轮落图
  bool keepRunning  = true;

    while (keepRunning)
  {
    if (thicknessPanel != null && thicknessPanel.Cancelled)
      break;

    // 2. 选取本轮第一个点（起点）
    var gpStart = new GetPoint();
    string promptStart = anyCommitted
      ? "选择墙轴线第一个点（Enter 结束命令）"
      : "选择墙轴线第一个点";

    gpStart.SetCommandPrompt(promptStart);

    // 只有当已经生成过墙线时，才允许“空回车”直接结束命令
    gpStart.AcceptNothing(anyCommitted);

    GetResult resStart = gpStart.Get();

    if (resStart == GetResult.Cancel || (thicknessPanel != null && thicknessPanel.Cancelled))
      break;

    if (resStart == GetResult.Nothing)
      break;

    // ★ 每一轮：用户点下这一轮的第一个点之后，再读取“当前激活图层”
    int baseLayerIndex = doc.Layers.CurrentLayerIndex;
    if (baseLayerIndex < 0)
    {
      // 没有有效图层，就结束整个命令
      keepRunning = false;
      break;
    }

    Layer baseLayer = doc.Layers[baseLayerIndex];
    Guid parentId   = baseLayer.ParentLayerId;   // 用当前激活图层的父层，作为这“一轮”的分区

    if (parentId == Guid.Empty) parentId = baseLayer.Id;
    if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
    return;


    Point3d firstPt = gpStart.Point();
    Point3d currentStart = firstPt;

    // 本轮的轴线集合（只在当前轮里预览 + 最后一次性落图）
    List<AxisInfo> currentAxes = new List<AxisInfo>();

    bool drawing = true;

    while (drawing)
    {
      if (thicknessPanel != null && thicknessPanel.Cancelled)
      {
        currentAxes.Clear();
        keepRunning = false;
        drawing = false;
        break;
      }

      var gp = new GetPoint();

      string modeText = (mode == AxisDrawMode.Line) ? "直线" : "圆弧";
       WallKind wallKindNow = thicknessPanel != null
        ? thicknessPanel.CurrentWallKind
        : currentWallType;
      string wallText = GetWallKindNameCn(wallKindNow);

      gp.SetCommandPrompt(
        $"选择下一个点（当前：{modeText}，{wallText}；Enter 结束本条墙）");

      gp.SetBasePoint(currentStart, true);
      gp.DrawLineFromPoint(currentStart, false); // 不画默认灰线

      int optLine      = gp.AddOption("直线");
      int optArc       = gp.AddOption("圆弧");
      int optConcrete  = gp.AddOption("钢筋砼墙");
      int optBlock     = gp.AddOption("填充墙");
      int optGlass     = gp.AddOption("玻璃幕墙");
      int optStud      = gp.AddOption("轻质隔墙");

      gp.AcceptNothing(true);   // ★ 允许“空回车/右键”结束当前这条墙

      // 动态预览：本轮已完成段 + 当前段
      gp.DynamicDraw += (sender, e) =>
      {
        // 当前用于预览的墙类型
        WallKind kindForPreview = thicknessPanel != null
          ? thicknessPanel.CurrentWallKind
          : currentWallType;
        // 已完成段（仅当前轮）
        foreach (var info in currentAxes)
        {
          if (info.AxisCurve != null)
            e.Display.DrawCurve(info.AxisCurve, axisPreviewColor);
          if (info.WallLeft != null)
            e.Display.DrawCurve(info.WallLeft, leftPreviewColor);
          if (info.WallRight != null)
            e.Display.DrawCurve(info.WallRight, rightPreviewColor);
          if (info.InsulLeftCurve != null && info.InsulLeftOn)
            e.Display.DrawCurve(info.InsulLeftCurve, insulLeftPreviewColor);
          if (info.InsulRightCurve != null && info.InsulRightOn)
            e.Display.DrawCurve(info.InsulRightCurve, insulRightPreviewColor);
        }

        Point3d cur = e.CurrentPoint;
        if (cur.DistanceTo(currentStart) < tol) return;

        // 从面板读当前 a,b, ai, bi + 勾选状态
        double a  = 0.0;
        double b  = 0.0;
        double ai = 0.0;
        double bi = 0.0;
        bool   insulLOn   = false;
        bool   insulROn   = false;
        bool   outlineL   = false;
        bool   outlineR   = false;

        if (thicknessPanel != null)
        {
          a = thicknessPanel.LeftValue;
          b = thicknessPanel.RightValue;

          ai = thicknessPanel.InsulLeftValue;
          bi = thicknessPanel.InsulRightValue;

          insulLOn = thicknessPanel.InsulLeftOn;
          insulROn = thicknessPanel.InsulRightOn;

          outlineL = thicknessPanel.UseInsulLeftAsOutline;
          outlineR = thicknessPanel.UseInsulRightAsOutline;
        }

        // 只有在对应侧保温“开”的时候，ai / bi 才参与 L/R 修正
        double aiForWall = insulLOn ? ai : 0.0;
        double biForWall = insulROn ? bi : 0.0;

        double offsetL, offsetR;
        ComputeWallOffsets(a, b, aiForWall, biForWall,
                           outlineL, outlineR,
                           out offsetL, out offsetR);

        if (mode == AxisDrawMode.Line)
        {
          Line seg = new Line(currentStart, cur);
          if (!seg.IsValid) return;

          Curve axisCrv = seg.ToNurbsCurve();
          e.Display.DrawCurve(axisCrv, axisPreviewColor);

          AxisInfo tempAxis = new AxisInfo
          {
            AxisCurve             = axisCrv,
            Start                 = seg.From,
            End                   = seg.To,
            OffsetL               = offsetL,
            OffsetR               = offsetR,
            WallType              = thicknessPanel != null ? thicknessPanel.CurrentWallKind : currentWallType,
            AxisType              = AxisCurveKind.Line,   // ★ 新增曲线类型《《《《《
            InsulLeftOffset       = ai,
            InsulRightOffset      = bi,
            InsulLeftOn           = insulLOn,
            InsulRightOn          = insulROn,
            UseInsulLeftAsOutline  = outlineL,
            UseInsulRightAsOutline = outlineR
          };

          Plane pl;
          if (!BuildAxisPlane(tempAxis, out pl)) return;

          PreviewOffsetWalls(axisCrv, pl, offsetL, offsetR, tol,
                             e.Display, leftPreviewColor, rightPreviewColor, tempAxis.WallType);

          PreviewInsulationWalls(axisCrv, pl,
                                 offsetL, offsetR,
                                 ai, bi,
                                 insulLOn, insulROn,
                                 tol,
                                 e.Display,
                                 insulLeftPreviewColor,
                                 insulRightPreviewColor);
        }
        else
        {
          // 圆弧模式第一步：先画 chord 线，只给个大致方向感
          Line seg = new Line(currentStart, cur);
          if (!seg.IsValid) return;

          Curve axisCrv = seg.ToNurbsCurve();
          e.Display.DrawCurve(axisCrv, axisPreviewColor);

          AxisInfo tempAxis = new AxisInfo
          {
            AxisCurve             = axisCrv,
            Start                 = seg.From,
            End                   = seg.To,
            OffsetL               = offsetL,
            OffsetR               = offsetR,
            WallType              = currentWallType,
            AxisType              = AxisCurveKind.Arc,   // ★ 新增曲线类型《《《《
            InsulLeftOffset       = ai,
            InsulRightOffset      = bi,
            InsulLeftOn           = insulLOn,
            InsulRightOn          = insulROn,
            UseInsulLeftAsOutline  = outlineL,
            UseInsulRightAsOutline = outlineR
          };

          Plane pl;
          if (!BuildAxisPlane(tempAxis, out pl)) return;

          PreviewOffsetWalls(axisCrv, pl, offsetL, offsetR, tol,
                             e.Display, leftPreviewColor, rightPreviewColor, tempAxis.WallType);

          PreviewInsulationWalls(axisCrv, pl,
                                 offsetL, offsetR,
                                 ai, bi,
                                 insulLOn, insulROn,
                                 tol,
                                 e.Display,
                                 insulLeftPreviewColor,
                                 insulRightPreviewColor);
        }
      };

      GetResult res = gp.Get();

      // 2）Esc：取消本轮，并结束命令（不影响之前已落图墙线）
      if (res == GetResult.Cancel)
      {
        currentAxes.Clear();
        keepRunning = false;
        drawing = false;
        break;
      }

      if (thicknessPanel != null && thicknessPanel.Cancelled)
      {
        currentAxes.Clear();
        keepRunning = false;
        drawing = false;
        break;
      }

      if (res == GetResult.Point)
      {
        Point3d picked = gp.Point();
        if (picked.DistanceTo(currentStart) < tol)
        {
          currentStart = picked;
          continue;
        }

        // ===== 直线模式：两点成段 =====
        if (mode == AxisDrawMode.Line)
        {
          Line seg = new Line(currentStart, picked);
          if (!seg.IsValid || seg.Length <= tol)
          {
            currentStart = picked;
            continue;
          }

          // 锁定当前段的 a,b,ai,bi + 勾选状态
          double a  = 0.0;
          double b  = 0.0;
          double ai = 0.0;
          double bi = 0.0;
          bool   insulLOn   = false;
          bool   insulROn   = false;
          bool   outlineL   = false;
          bool   outlineR   = false;

          if (thicknessPanel != null)
          {
            a = thicknessPanel.LeftValue;
            b = thicknessPanel.RightValue;

            ai = thicknessPanel.InsulLeftValue;
            bi = thicknessPanel.InsulRightValue;

            insulLOn = thicknessPanel.InsulLeftOn;
            insulROn = thicknessPanel.InsulRightOn;

            outlineL = thicknessPanel.UseInsulLeftAsOutline;
            outlineR = thicknessPanel.UseInsulRightAsOutline;
          }

          double aiForWall = insulLOn ? ai : 0.0;
          double biForWall = insulROn ? bi : 0.0;

          double offsetL, offsetR;
          ComputeWallOffsets(a, b, aiForWall, biForWall,
                             outlineL, outlineR,
                             out offsetL, out offsetR);

          Curve axisCrv = seg.ToNurbsCurve();

          AxisInfo axisInfo = new AxisInfo
          {
            AxisCurve             = axisCrv,
            Start                 = seg.From,
            End                   = seg.To,
            OffsetL               = offsetL,
            OffsetR               = offsetR,
            WallType              = thicknessPanel != null ? thicknessPanel.CurrentWallKind : currentWallType,
            AxisType              = AxisCurveKind.Line,   // ★ 新增
            InsulLeftOffset       = ai,
            InsulRightOffset      = bi,
            InsulLeftOn           = insulLOn,
            InsulRightOn          = insulROn,
            UseInsulLeftAsOutline  = outlineL,
            UseInsulRightAsOutline = outlineR
          };

          Plane pl;
          if (BuildAxisPlane(axisInfo, out pl))
          {
            Curve wallL, wallR;
            BuildOffsetWallPair(axisInfo, pl, tol, out wallL, out wallR);
            axisInfo.WallLeft  = wallL;
            axisInfo.WallRight = wallR;

            Curve insCurveL, insCurveR;
            BuildInsulationPair(axisInfo, pl, tol, out insCurveL, out insCurveR);
            axisInfo.InsulLeftCurve  = insCurveL;
            axisInfo.InsulRightCurve = insCurveR;
          }

          currentAxes.Add(axisInfo);
          currentStart = picked;
          continue;
        }
        // ===== 圆弧模式：picked 作为中点，再要一个终点 =====
        else
        {
          Point3d midPt = picked;

          var gp2 = new GetPoint();
          gp2.SetCommandPrompt("选择圆弧终点（Enter 结束本条墙）");
          gp2.SetBasePoint(currentStart, true);
          gp2.DrawLineFromPoint(currentStart, false);

          gp2.AcceptNothing(true);  // ★ 让 Enter 成为“结束圆弧”，而不是 Cancel

          gp2.DynamicDraw += (sender2, e2) =>
          {
            WallKind kindForPreview2 = thicknessPanel != null
            ? thicknessPanel.CurrentWallKind
            : currentWallType;

            foreach (var info in currentAxes)
            {
              if (info.AxisCurve != null)
                e2.Display.DrawCurve(info.AxisCurve, axisPreviewColor);
              if (info.WallLeft != null)
                e2.Display.DrawCurve(info.WallLeft, leftPreviewColor);
              if (info.WallRight != null)
                e2.Display.DrawCurve(info.WallRight, rightPreviewColor);
              if (info.InsulLeftCurve != null && info.InsulLeftOn)
                e2.Display.DrawCurve(info.InsulLeftCurve, insulLeftPreviewColor);
              if (info.InsulRightCurve != null && info.InsulRightOn)
                e2.Display.DrawCurve(info.InsulRightCurve, insulRightPreviewColor);
            }

            Point3d cur2 = e2.CurrentPoint;
            if (cur2.DistanceTo(currentStart) < tol || cur2.DistanceTo(midPt) < tol) return;

            Arc arcTmp = new Arc(currentStart, midPt, cur2);
            if (!arcTmp.IsValid)
            {
              e2.Display.DrawLine(currentStart, cur2, axisPreviewColor);
              return;
            }

            Curve axisCrvTmp = arcTmp.ToNurbsCurve();
            e2.Display.DrawCurve(axisCrvTmp, axisPreviewColor);

            double a  = 0.0;
            double b  = 0.0;
            double ai = 0.0;
            double bi = 0.0;
            bool   insulLOn   = false;
            bool   insulROn   = false;
            bool   outlineL   = false;
            bool   outlineR   = false;

            if (thicknessPanel != null)
            {
              a = thicknessPanel.LeftValue;
              b = thicknessPanel.RightValue;

              ai = thicknessPanel.InsulLeftValue;
              bi = thicknessPanel.InsulRightValue;

              insulLOn = thicknessPanel.InsulLeftOn;
              insulROn = thicknessPanel.InsulRightOn;

              outlineL = thicknessPanel.UseInsulLeftAsOutline;
              outlineR = thicknessPanel.UseInsulRightAsOutline;
            }

            double aiForWall = insulLOn ? ai : 0.0;
            double biForWall = insulROn ? bi : 0.0;

            double offsetL2, offsetR2;
            ComputeWallOffsets(a, b, aiForWall, biForWall,
                               outlineL, outlineR,
                               out offsetL2, out offsetR2);

            AxisInfo tempAxis = new AxisInfo
            {
              AxisCurve             = axisCrvTmp,
              Start                 = arcTmp.StartPoint,
              End                   = arcTmp.EndPoint,
              OffsetL               = offsetL2,
              OffsetR               = offsetR2,
              WallType              = kindForPreview2,
              AxisType              = AxisCurveKind.Arc,    // ★ 新增
              InsulLeftOffset       = ai,
              InsulRightOffset      = bi,
              InsulLeftOn           = insulLOn,
              InsulRightOn          = insulROn,
              UseInsulLeftAsOutline  = outlineL,
              UseInsulRightAsOutline = outlineR
            };

            Plane pl;
            if (!BuildAxisPlane(tempAxis, out pl)) return;

            PreviewOffsetWalls(axisCrvTmp, pl, offsetL2, offsetR2, tol,
                               e2.Display, leftPreviewColor, rightPreviewColor, tempAxis.WallType);

            PreviewInsulationWalls(axisCrvTmp, pl,
                                   offsetL2, offsetR2,
                                   ai, bi,
                                   insulLOn, insulROn,
                                   tol,
                                   e2.Display,
                                   insulLeftPreviewColor,
                                   insulRightPreviewColor);
          };

          GetResult res2 = gp2.Get();

          // Esc：取消本轮，并结束命令
          if (res2 == GetResult.Cancel)
          {
            currentAxes.Clear();
            keepRunning = false;
            drawing = false;
            break;
          }

          if (thicknessPanel != null && thicknessPanel.Cancelled)
          {
            currentAxes.Clear();
            keepRunning = false;
            drawing = false;
            break;
          }

          if (res2 == GetResult.Point)
          {
            Point3d endPt = gp2.Point();
            if (endPt.DistanceTo(currentStart) < tol || endPt.DistanceTo(midPt) < tol)
            {
              currentStart = endPt;
              continue;
            }

            Arc arc = new Arc(currentStart, midPt, endPt);
            if (!arc.IsValid)
            {
              currentStart = endPt;
              continue;
            }

            double a  = 0.0;
            double b  = 0.0;
            double ai = 0.0;
            double bi = 0.0;
            bool   insulLOn   = false;
            bool   insulROn   = false;
            bool   outlineL   = false;
            bool   outlineR   = false;

            if (thicknessPanel != null)
            {
              a = thicknessPanel.LeftValue;
              b = thicknessPanel.RightValue;

              ai = thicknessPanel.InsulLeftValue;
              bi = thicknessPanel.InsulRightValue;

              insulLOn = thicknessPanel.InsulLeftOn;
              insulROn = thicknessPanel.InsulRightOn;

              outlineL = thicknessPanel.UseInsulLeftAsOutline;
              outlineR = thicknessPanel.UseInsulRightAsOutline;
            }

            double aiForWall = insulLOn ? ai : 0.0;
            double biForWall = insulROn ? bi : 0.0;

            double offsetL2, offsetR2;
            ComputeWallOffsets(a, b, aiForWall, biForWall,
                               outlineL, outlineR,
                               out offsetL2, out offsetR2);

            Curve axisCrv = arc.ToNurbsCurve();

            AxisInfo axisInfo = new AxisInfo
            {
              AxisCurve             = axisCrv,
              Start                 = arc.StartPoint,
              End                   = arc.EndPoint,
              OffsetL               = offsetL2,
              OffsetR               = offsetR2,
              WallType              = thicknessPanel != null ? thicknessPanel.CurrentWallKind : currentWallType,
              AxisType              = AxisCurveKind.Arc,   // ★ 加上这一行
              InsulLeftOffset       = ai,
              InsulRightOffset      = bi,
              InsulLeftOn           = insulLOn,
              InsulRightOn          = insulROn,
              UseInsulLeftAsOutline  = outlineL,
              UseInsulRightAsOutline = outlineR
            };

            Plane pl;
            if (BuildAxisPlane(axisInfo, out pl))
            {
              Curve wallL, wallR;
              BuildOffsetWallPair(axisInfo, pl, tol, out wallL, out wallR);
              axisInfo.WallLeft  = wallL;
              axisInfo.WallRight = wallR;

              Curve insCurveL, insCurveR;
              BuildInsulationPair(axisInfo, pl, tol, out insCurveL, out insCurveR);
              axisInfo.InsulLeftCurve  = insCurveL;
              axisInfo.InsulRightCurve = insCurveR;
            }

            currentAxes.Add(axisInfo);
            currentStart = endPt;
            continue;
          }
          else
          {
            // 第二步直接回车：结束本条墙（保持已记录的 currentAxes，不再新增段）
            drawing = false;
            break;
          }
        }
      }
      else if (res == GetResult.Option)
      {
        CommandLineOption opt = gp.Option();
        if (opt != null)
        {
                    if (opt.Index == optLine)
          {
            mode = AxisDrawMode.Line;
            thicknessPanel?.SetShapeMode(AxisDrawMode.Line, false); // 同步按钮高亮，不触发事件
          }
          else if (opt.Index == optArc)
          {
            mode = AxisDrawMode.Arc;
            thicknessPanel?.SetShapeMode(AxisDrawMode.Arc, false);
          }
          else if (opt.Index == optConcrete)
          {
            currentWallType = WallKind.Concrete;
            thicknessPanel?.SetWallKind(WallKind.Concrete);
          }
          else if (opt.Index == optBlock)
          {
            currentWallType = WallKind.Block;
            thicknessPanel?.SetWallKind(WallKind.Block);
          }
          else if (opt.Index == optGlass)
          {
            currentWallType = WallKind.Glass;
            thicknessPanel?.SetWallKind(WallKind.Glass);
          }
          else if (opt.Index == optStud)
          {
            currentWallType = WallKind.Stud;
            thicknessPanel?.SetWallKind(WallKind.Stud);
          }

        }
        continue;
      }
      else
      {
        // 回车 / 右键：结束当前这条墙，准备落图
        drawing = false;
        break;
      }
    } // end while(drawing)


    // 本轮如果有有效墙段，就立即落图（之后不再可被取消）

    if (currentAxes.Count > 0)
     {
        // ★ 提交前：检查 new vs old（重叠则终止本次会话）
        if (AbortIfAnyAxisOverlap(
        doc,
        parentId,
        currentAxes.Where(a => a != null).Select(a => a.AxisCurve),
        tol))
        {
            // 你这里“预览”本质是 DynamicDraw，不一定有临时物件可删；
            // 最少把本轮缓存清掉即可
            currentAxes.Clear();
            doc.Views.Redraw();

            keepRunning = false; // 按你建议：直接结束外层 while
        }
        else
        {
            ProcessNewAxesForParent(doc, parentId, currentAxes);
            anyCommitted = true;
        }
     }

    if (!keepRunning)
      break;
  }
   }
  finally
  {
    // ★ 不管是正常结束还是 ESC / 关闭面板，这里都会被执行
    thicknessPanel.IsDrawingWalls = false;
  }
}

// ====== 单线变墙：支持动态预览期间鼠标单击翻转轴线方向（从而翻转左右墙线/保温线） ======
int FindNearestAxisIndex(List<Curve> axes, Point3d pick, double maxDist, out double bestDist)
{
  bestDist = double.MaxValue;
  if (axes == null || axes.Count == 0) return -1;

  int bestIdx = -1;

  for (int i = 0; i < axes.Count; i++)
  {
    var c = axes[i];
    if (c == null || !c.IsValid) continue;

    double t;
    if (!c.ClosestPoint(pick, out t)) continue;

    var cp = c.PointAt(t);
    double d = pick.DistanceTo(cp);

    if (d < bestDist)
    {
      bestDist = d;
      bestIdx = i;
    }
  }

  if (bestIdx < 0) return -1;
  if (bestDist > maxDist) return -1;
  return bestIdx;
}

void RunSingleToWallMode(RhinoDoc doc, ThicknessPanel thicknessPanel)
{
  if (doc == null) return;
  if (thicknessPanel == null) return;
  if (thicknessPanel.Cancelled) return;

  // 已经在单线变墙流程里了，就不要再进一遍
  if (thicknessPanel.IsSingleToWallMode)
  {
    RhinoApp.WriteLine("单线变墙命令正在执行中");
    return;
  }

  // 第一次使用该命令：重置一次默认参数（100/100、保温关、外轮廓关、填充墙）
  if (!RTZ3State.HasUsedSingleToWallOnce)
   {
    thicknessPanel.ResetForSingleToWallDefaults();
    RTZ3State.HasUsedSingleToWallOnce = true;
   }

  // 面板进入“单线变墙”模式
  thicknessPanel.SetWallGenerateMode(WallGenerateMode.SingleToWall);
  thicknessPanel.SetSingleToWallMode(true);
  RhinoApp.WriteLine("请选择当前分区【父级图层】下的曲线");

  double tol = doc.ModelAbsoluteTolerance;

  try
  {
    // ===================== 连续多轮：直到用户 Enter 空选结束 / Esc 退出 / 关闭面板 =====================
    while (true)
    {
      if (thicknessPanel.Cancelled) break;

      // ★ 每一轮都重新读取“当前激活图层”，推导本分区 parentId（父级为空就用自己）
        Guid parentId = Guid.Empty;
        int curLi = doc.Layers.CurrentLayerIndex;
        if (curLi >= 0 && curLi < doc.Layers.Count)
        {
         var curLay = doc.Layers[curLi];
         if (curLay != null)
         {
            parentId = curLay.ParentLayerId;
            if (parentId == Guid.Empty) parentId = curLay.Id;
         }
        }

      if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
      return;

      // ---------- 1) 选择要转换的曲线（Enter 空选 = 结束单线变墙） ----------
      var go = new GetObject();
      go.SetCommandPrompt("请选择要转换为墙体的曲线，Enter 结束单线变墙");

      go.GeometryFilter               = ObjectType.Curve;
      go.SubObjectSelect              = false;
      go.EnablePreSelect(true, true);
      go.EnablePostSelect(true);
      go.DeselectAllBeforePostSelect  = false;


      // 允许 0 个：用户 Enter 空选时退出
      GetResult res = go.GetMultiple(0, 0);

      if (res == GetResult.Cancel)
      {
        // Esc 在“选择阶段” -> 直接退出整个单线变墙
        break;
      }

      if (res != GetResult.Object || go.ObjectCount <= 0)
      {
        // Enter 空选 -> 正常结束整个单线变墙
        RhinoApp.WriteLine("单线变墙结束");
        break;
      }

        // ===== 新增：过滤掉“非本父级图层树”的选择（提示 + 自动取消选择） =====
        var validRefs = new List<ObjRef>();
        int invalidCount = 0;

        for (int i = 0; i < go.ObjectCount; i++)
      {
        var objRef = go.Object(i);
        if (objRef == null) continue;

        var obj = objRef.Object();
        var crv = objRef.Curve();
        if (obj == null || crv == null) continue;

        int li = obj.Attributes.LayerIndex;
        bool inParent = (parentId != Guid.Empty && li >= 0 && li < doc.Layers.Count && IsLayerUnderParent(doc, li, parentId));

        if (!inParent)
        {
            invalidCount++;
            obj.Select(false); // 自动取消这条选择
            continue;
        }

        validRefs.Add(objRef);
      }

        if (invalidCount > 0)
      {
        RhinoApp.WriteLine("提示：请选择当前分区（父级图层）下的曲线", invalidCount);
        doc.Views.Redraw();
      }

        if (validRefs.Count == 0)
      {
        // 用户这次选的全是外部分区：提示完，回到选择阶段继续选
        continue;
      }






      int count = validRefs.Count;

      // 记录原始曲线 Id：确认生成后删除源曲线
      var sourceCurveIds = new List<Guid>();

      // ---------- 2) 将选择的曲线拆段 + 平滑合并（预览和最终生成共用同一份 mergedCurves，确保 flip 会生效） ----------
      var segments = new List<Curve>();

      for (int i = 0; i < count; i++)
      {
        var objRef = validRefs[i];
        if (objRef == null) continue;

        var obj = objRef.Object();
        var crv = objRef.Curve();
        if (obj == null || crv == null) continue;
        if (!crv.IsValid || crv.IsShort(tol)) continue;

        sourceCurveIds.Add(obj.Id);

        Curve[] pieces = null;

        var poly = crv as PolyCurve;
        if (poly != null)
        {
          pieces = poly.Explode();
        }
        else
        {
          var plc = crv as PolylineCurve;
          if (plc != null)
            pieces = plc.DuplicateSegments();
        }

        if (pieces != null && pieces.Length > 0)
        {
          foreach (var seg in pieces)
          {
            if (seg == null || !seg.IsValid || seg.IsShort(tol)) continue;
            // 这里 seg 本身就是新对象，不污染原曲线
            segments.Add(seg);
          }
        }
        else
        {
          var dup = crv.DuplicateCurve();
          if (dup != null && dup.IsValid && !dup.IsShort(tol))
            segments.Add(dup);
        }
      }

      if (segments.Count == 0)
      {
        RhinoApp.WriteLine("选中的曲线都不可用，本轮跳过。");
        continue;
      }

      var mergedCurves = JoinSmoothSegmentsForSingleToWall(segments, tol);
      if (mergedCurves == null || mergedCurves.Count == 0)
      {
        RhinoApp.WriteLine("曲线合并后为空，本轮跳过。");
        continue;
      }

      // ---------- 3) 开启预览 Conduit ----------
      RTZ3SingleToWallPreviewConduit previewConduit = null;
      previewConduit = new RTZ3SingleToWallPreviewConduit(doc, thicknessPanel);
      previewConduit.AxisCurves.Clear();
      previewConduit.AxisCurves.AddRange(mergedCurves);
      previewConduit.Enabled = true;
      doc.Views.Redraw();

      RhinoApp.WriteLine("预览阶段：单击轴线附近可翻转方向（距离<500）；Enter 生成；Esc 取消本轮。");

      bool cancelledThisRound = false;
      bool confirmedThisRound = false;
      bool quitAll = false;

      // ---------- 4) 预览交互循环：GetPoint 负责“单击翻转 / Enter确认 / Esc取消本轮” ----------
      while (true)
      {
        if (thicknessPanel.Cancelled)
        {
          cancelledThisRound = true;
          break;
        }

        var gp = new GetPoint();
        gp.SetCommandPrompt("单击轴线附近( <500 )翻转方向；Enter 生成本轮；Esc 取消命令");
        gp.AcceptNothing(true); // Enter / 空格 -> Nothing

        var r = gp.Get();

        if (r == GetResult.Cancel)
        {
          // Esc：取消本轮（不退出整个单线变墙）
          quitAll = true;
          break;
        }

        if (r == GetResult.Nothing)
        {
          // Enter/空格：确认生成
          confirmedThisRound = true;
          break;
        }

        if (r == GetResult.Point)
        {
          // 鼠标单击：只做 flip，不结束命令
          Point3d pick = gp.Point();

          double bestDist;
          int idx = FindNearestAxisIndex(mergedCurves, pick, 500.0, out bestDist);
          if (idx < 0)
          {
            // 远点误触：忽略
            continue;
          }

          var target = mergedCurves[idx];
          if (target != null && target.IsValid)
          {
            // 翻转轴线方向（起终点互换） -> 左右墙线/保温线自然随之翻转
            target.Reverse();
            doc.Views.Redraw();
          }

          // 继续等待下一次点击/确认
          continue;
        }

        // 其它结果：忽略继续
      }

      // ---------- 5) 关闭本轮预览 ----------
      if (previewConduit != null)
      {
        previewConduit.Enabled = false;
        doc.Views.Redraw();
      }

      if (quitAll)
      {
        RhinoApp.WriteLine("单线变墙已取消。");
        break; // 跳出外层 while(true)，触发 finally 复位 UI
      }


      if (cancelledThisRound)
      {
        RhinoApp.WriteLine("本轮单线变墙已取消。");
        // 继续下一轮选择
        continue;
      }

      if (!confirmedThisRound)
      {
        // 理论上不会走到这，但保险
        RhinoApp.WriteLine("本轮未确认，跳过。");
        continue;
      }

      // ---------- 6) 用户确认：用“当前面板参数 + 已被 flip 的 mergedCurves”正式生成 ----------
      int baseLayerIndex = doc.Layers.CurrentLayerIndex;
      if (baseLayerIndex < 0) continue;

      Layer baseLayer = doc.Layers[baseLayerIndex];
      // ★ 不要重新声明，直接复用本轮 parentId（这里重算一次也没问题）
      parentId = baseLayer.ParentLayerId;
      if (parentId == Guid.Empty) parentId = baseLayer.Id;

      // 读取面板参数（确认时再读一次，确保是最后确定值）
      double a  = thicknessPanel.LeftValue;
      double b  = thicknessPanel.RightValue;
      double ai = thicknessPanel.InsulLeftValue;
      double bi = thicknessPanel.InsulRightValue;

      bool insulLOn = thicknessPanel.InsulLeftOn;
      bool insulROn = thicknessPanel.InsulRightOn;
      bool outlineL = thicknessPanel.UseInsulLeftAsOutline;
      bool outlineR = thicknessPanel.UseInsulRightAsOutline;

      double aiForWall = insulLOn ? ai : 0.0;
      double biForWall = insulROn ? bi : 0.0;

      double offsetL, offsetR;
      ComputeWallOffsets(a, b, aiForWall, biForWall, outlineL, outlineR, out offsetL, out offsetR);

      WallKind wallKind = thicknessPanel.CurrentWallKind;

      var axes = new List<AxisInfo>();

      foreach (var curve in mergedCurves)
      {
        if (curve == null || !curve.IsValid || curve.IsShort(tol))
          continue;

        // ★关键：几何判定 + 归一化
        Curve axisCrvNorm;
        AxisCurveKind axisKind = GetAxisKindAndNormalize(curve, tol, out axisCrvNorm);

        var axisInfo = new AxisInfo
        {
          AxisCurve              = axisCrvNorm,
          Start                  = axisCrvNorm.PointAtStart,
          End                    = axisCrvNorm.PointAtEnd,
          OffsetL                = offsetL,
          OffsetR                = offsetR,
          WallType               = wallKind,
          AxisType               = axisKind,
          InsulLeftOffset        = ai,
          InsulRightOffset       = bi,
          InsulLeftOn            = insulLOn,
          InsulRightOn           = insulROn,
          UseInsulLeftAsOutline  = outlineL,
          UseInsulRightAsOutline = outlineR
        };

        Plane pl;
        if (BuildAxisPlane(axisInfo, out pl))
        {
          Curve wallL, wallR;
          BuildOffsetWallPair(axisInfo, pl, tol, out wallL, out wallR);
          axisInfo.WallLeft  = wallL;
          axisInfo.WallRight = wallR;

          Curve insL, insR;
          BuildInsulationPair(axisInfo, pl, tol, out insL, out insR);
          axisInfo.InsulLeftCurve  = insL;
          axisInfo.InsulRightCurve = insR;
        }

        axes.Add(axisInfo);
      }

      if (axes.Count == 0)
      {
        RhinoApp.WriteLine("本轮没有有效轴线，跳过。");
        continue;
      }

      RTZ3State.SuppressCapsOnce = true;
      // 写入并处理（你原来的通用管线）

        // ★ 兼容“复制/移动出来的 Wallaxis 仍在 PB-Wall-Axis 上”的情况：
        //    否则会在 AbortIfAnyAxisOverlap 里被当作“旧轴线”，与本轮 axes 发生“自重叠误判”。
        int axisLi0 = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
        if (axisLi0 >= 0)
        {
            var axisIdsInSource = new List<Guid>();

            foreach (var id in sourceCurveIds)
            {
            var ro = doc.Objects.FindId(id) as CurveObject;
            if (ro == null) continue;
            if (ro.Attributes.LayerIndex != axisLi0) continue;

            string nm = ro.Attributes.Name ?? "";
            if (!nm.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
            continue;

            axisIdsInSource.Add(id);
            
            }

            if (axisIdsInSource.Count > 0)
            {
            // 如果这些源轴线本来就带着旧墙线/旧封口（比如移动的是一根已生成过墙线的轴线），先清理掉
            DeleteWallCapsByAxisIds(doc, axisIdsInSource, false);
            DeleteAllWallsAndInsulationForAxes(doc, axisIdsInSource);

            // 再把“源 Wallaxis 本体”从旧集合里移除，避免 overlap 误判
            // 再把“源 Wallaxis 本体”从旧集合里移除，避免 overlap 误判
            bool axisLayerWasLocked0 = false;
            var axisLayer0 = (axisLi0 >= 0 && axisLi0 < doc.Layers.Count) ? doc.Layers[axisLi0] : null;
            if (axisLayer0 != null && axisLayer0.IsLocked)
            {   
            axisLayerWasLocked0 = true;
            axisLayer0.IsLocked = false;
            doc.Layers.Modify(axisLayer0, axisLi0, true);
            }

            try
            {
            foreach (var id in axisIdsInSource)
             {
                var roDel = doc.Objects.FindId(id);
                if (roDel == null) continue;
                doc.Objects.Delete(roDel, true);
             }
            }
            finally
            {
            if (axisLayerWasLocked0 && axisLayer0 != null)
             {
                axisLayer0.IsLocked = true;
                doc.Layers.Modify(axisLayer0, axisLi0, true);
             }
            }

            }
        }
    
      if (AbortIfAnyAxisOverlap(doc, parentId, axes.Where(a=>a!=null).Select(a=>a.AxisCurve), tol))
      {
        RhinoApp.WriteLine("[RTZ3] 检测到与已存在轴线重叠：本轮提交已取消。");
        // 这里你可以选择：
        // A) continue;  // 取消本轮，回到“选择曲线”那一步
        break;     // 直接结束整个单线变墙命令
        continue;
      }


      ProcessNewAxesForParent(doc, parentId, axes);

      // 删除源曲线（只在成功确认后）
      foreach (var id in sourceCurveIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj != null)
          doc.Objects.Delete(obj, true);
      }
      // 然后再封口（此时场景里只剩“新轴线体系”）
      ApplyWallChamferForAxesToBeDeal(doc, parentId);
      ApplyWallCapsForAxesToBeDeal(doc);
      ApplyWallBigFallbackForOrphanEnds(doc, parentId);
      ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);

      doc.Views.Redraw();

      // ---------- 7) 自动下一轮：回到 while(true) 的选择阶段 ----------
    }
   }
   finally
   {
    // 退出整个单线变墙：复位 UI 状态
    thicknessPanel.SetSingleToWallMode(false);
    thicknessPanel.SetWallGenerateMode(WallGenerateMode.None);
    doc.Views.Redraw();
   }
}


void RunRefreshForCurrentParent(RhinoDoc doc, ThicknessPanel panel = null)
{
  if (doc == null) return;
double tol = doc.ModelAbsoluteTolerance;

  // ★ 端点连接判定用更宽松容差（默认至少 1mm）
  double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  double connectTol = Math.Max(tol, 1.0 * mm);
  double grid = connectTol;  // 给端点哈希用
    
  // 0）确定当前分区 parentId（你一直用这个做分区）
  Guid parentId = Guid.Empty;
  int curLi = doc.Layers.CurrentLayerIndex;
  if (curLi >= 0 && curLi < doc.Layers.Count)
  {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
      parentId = curLay.ParentLayerId; // 顶层则为 Guid.Empty
      if (parentId == Guid.Empty)
        parentId = curLay.Id;          // ★ 顶层分区：用自己当 parent
    }
  }

  // parentId 已经算完
  // ★ 兜底：无论中途在哪个 return 退出，最后都强制把墙相关图层锁回去
  void ForceRelockWallRelatedLayers()
  {
   try
   {
    int w = -1, wg = -1, ws = -1, ins = -1;
    LockExistingWallLayers(doc, parentId, ref w, ref wg, ref ws, ref ins);
    ForceLockWallHatchLayer(doc, parentId);
   }
   catch { }
  }

 try
 {
  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;

  // 1）找到本分区下 PB-Wall-Axis
  int axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLayerIndex < 0)
  {
    RhinoApp.WriteLine("[Refresh] 当前分区未找到 PB-Wall-Axis，取消刷新。");
    return;
  }

  var axisLayer = doc.Layers[axisLayerIndex];
  if (axisLayer == null)
  {
    RhinoApp.WriteLine("[Refresh] PB-Wall-Axis 图层异常，取消刷新。");
    return;
  }

  // ★ 允许 axisObjs 为空：删轴线场景也要继续走 3.8 扫孤儿墙线
  var axisObjs = doc.Objects.FindByLayer(axisLayer);
  if (axisObjs == null) axisObjs = new RhinoObject[0];

  // 2）扫描轴线：
  //   2.1 建立“当前真实端点”索引（用于 3.7 / 3.8 找连接轴线）
  //   2.2 找出“被移动/编辑但 Name 未同步”的轴线（你的 dirty 规则）
  //   2.3 记录 dirty 轴线 Name 中的旧 Start/End，作为 3.7 的种子
  var dirtyAxisSet = new HashSet<Guid>();
  var oldEndpointSeeds = new List<Point3d>(); // 3.7 用：dirty轴线改动前端点（来自Name）

  var endpointIndex = new Dictionary<PtKey, HashSet<Guid>>();
  var liveAxisMap = new Dictionary<Guid, CurveObject>();

  void ExpandByCurrentEndpointNeighbors(
  Dictionary<PtKey, HashSet<Guid>> endpointIndex,
  Dictionary<Guid, CurveObject> liveAxisMap,
  List<Guid> ids,
  double connectTol,
  double grid)
  {
  var set = new HashSet<Guid>(ids);
  var seeds = ids.ToArray();

  foreach (var id in seeds)
  {
    if (!liveAxisMap.TryGetValue(id, out var obj) || obj == null) continue;
    var crv = obj.Geometry as Curve;
    if (crv == null || !crv.IsValid) continue;

    Point3d[] pts = { crv.PointAtStart, crv.PointAtEnd };
    foreach (var p in pts)
    {
      foreach (var candId in QueryNearEndpoint(endpointIndex, p, grid))
      {
        if (candId == id) continue;
        if (!liveAxisMap.TryGetValue(candId, out var candObj) || candObj == null) continue;
        var cc = candObj.Geometry as Curve;
        if (cc == null || !cc.IsValid) continue;

        if (Math.Min(p.DistanceTo(cc.PointAtStart), p.DistanceTo(cc.PointAtEnd)) <= connectTol)
          set.Add(candId);
      }
    }
  }

  ids.Clear();
  ids.AddRange(set);
 }


  foreach (var o in axisObjs)
  {
    var co = o as CurveObject;
    if (co == null) continue;

    var crv = co.Geometry as Curve;
    if (crv == null || !crv.IsValid) continue;

    string n = co.Attributes.Name;

    // ★ 新增：Name 为空的一律当空气（用户随手画的，不参与墙体系）
    if (string.IsNullOrWhiteSpace(n))
    continue;

    if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
    continue;


    liveAxisMap[co.Id] = co;

    // ★ 2.1 索引“当前真实端点”
    var geoStart = crv.PointAtStart;
    var geoEnd   = crv.PointAtEnd;
    IndexEndpoint(endpointIndex, geoStart, co.Id, grid);
    IndexEndpoint(endpointIndex, geoEnd,   co.Id, grid);

    // ★ 2.2 dirty 判定（沿用你现有逻辑）
    bool dirty = false;

    Guid idFromName;
    if (!TryParseAxisIdFromName(n, out idFromName))
    {
      dirty = true;
    }
    else
    {
      if (idFromName != co.Id) 
      {
      dirty = true;      
      }
    }

    if (!dirty)
    {
      Point3d nameStart, nameEnd;
      bool hasStart = TryParsePointFromName(n, "Start", out nameStart);
      bool hasEnd   = TryParsePointFromName(n, "End", out nameEnd);

      if (!hasStart || !hasEnd)
      {
        dirty = true;
      }
        else
      {
        // ★ 用 connectTol 判断“几何端点 vs Name端点”是否一致
        if (geoStart.DistanceTo(nameStart) > connectTol) dirty = true;
        if (geoEnd.DistanceTo(nameEnd) > connectTol) dirty = true;
      }
    }  

    if (dirty)
    {
      dirtyAxisSet.Add(co.Id);

      // ★ 2.3 记录“改动前端点”(来自Name)，用于 3.7 找旧连接轴线
      Point3d ns, ne;
      if (TryParsePointFromName(n, "Start", out ns)) oldEndpointSeeds.Add(ns);
      if (TryParsePointFromName(n, "End",   out ne)) oldEndpointSeeds.Add(ne);
    }
  }

  // 3）先做 3.7 / 3.8 / 3.9 —— 不要在这里因为 dirtyAxisSet==0 就 return（删轴线场景要靠 3.8）
  // ==================== 3.7：找“dirty轴线改动前连接的轴线”（端点相连）====================

  int orphanWallCurvesDeleted = 0; // 3.8 实际删除的孤儿墙线/保温线数量

  var connectedAxisSet = new HashSet<Guid>();

  foreach (var p in oldEndpointSeeds)
  {
    foreach (var candId in QueryNearEndpoint(endpointIndex, p, grid))
    {
      if (!liveAxisMap.TryGetValue(candId, out var candObj) || candObj == null) continue;
      var cc = candObj.Geometry as Curve;
      if (cc == null) continue;

      var s = cc.PointAtStart;
      var e = cc.PointAtEnd;

      if (Math.Min(p.DistanceTo(s), p.DistanceTo(e)) <= connectTol)
       {
        connectedAxisSet.Add(candId);
       }
    }
  }

  // 4）准备层索引（给 3.8 扫墙线 & SplitAxesAndRebuildWalls 用）
  int wallLayerIndex     = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
  int wallGlasLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
  int wallStudLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
  int insulLayerIndex    = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");

  // ==================== 3.8：找“被删除轴线”的遗留墙线，并用 AxisStart/AxisEnd 找连接轴线 ====================
  var connectedFromDeletedSet = new HashSet<Guid>();
  // ★ 新增：记录“墙线里写了AxisId但轴线不存在”的那些AxisId，用来删cap
  var missingAxisIds = new HashSet<Guid>(); 
  

  var lockBackup = new Dictionary<int, bool>();
  void EnsureUnlocked(int li)
  {
    if (li < 0 || li >= doc.Layers.Count) return;
    if (lockBackup.ContainsKey(li)) return;
    var ly = doc.Layers[li];
    if (ly == null) return;
    lockBackup[li] = ly.IsLocked;
    if (ly.IsLocked)
    {
      ly.IsLocked = false;
      doc.Layers.Modify(ly, li, true);
    }
  }

    IEnumerable<RhinoObject> EnumLayerCurves(int li)
 {
  if (li < 0 || li >= doc.Layers.Count) yield break;
  var ly = doc.Layers[li];
  if (ly == null) yield break;

  var objs = doc.Objects.FindByLayer(ly); // ✅ 数组快照，不是全场枚举器
  if (objs == null) yield break;

  foreach (var oo in objs)
    if (oo != null && oo.ObjectType == ObjectType.Curve)
      yield return oo;
 }



  var wallLayerList = new List<int>();
  if (wallLayerIndex >= 0) wallLayerList.Add(wallLayerIndex);
  if (wallGlasLayerIndex >= 0) wallLayerList.Add(wallGlasLayerIndex);
  if (wallStudLayerIndex >= 0) wallLayerList.Add(wallStudLayerIndex);
  if (insulLayerIndex >= 0) wallLayerList.Add(insulLayerIndex);

  foreach (var li in wallLayerList)
  {
    foreach (var obj in EnumLayerCurves(li))
    {
      string wn = obj.Attributes.Name ?? "";
      if (string.IsNullOrEmpty(wn)) continue;

      bool isWall = wn.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase) ||
                    wn.StartsWith("Wallinside|", StringComparison.OrdinalIgnoreCase);
      bool isIns  = wn.StartsWith("Insulation|", StringComparison.OrdinalIgnoreCase);
      if (!isWall && !isIns) continue;

      if (!TryParseAxisIdFromName(wn, out var axisIdInWall))
        continue;

      // ★ AxisId 找不到对应轴线 -> 说明这根轴线被删了（你的 3.8）
      var axisObj = doc.Objects.Find(axisIdInWall) as CurveObject;
      if (axisObj != null)
      {
        int ali = axisObj.Attributes.LayerIndex;
        if (ali >= 0 && ali < doc.Layers.Count && IsLayerUnderParent(doc, ali, parentId))
        {
        // 轴线在本父级 -> 正常认为存在
        continue;
        }
        // 轴线在别的父级 -> 对本分区来说等同“不存在”，继续走孤儿清理
      }
      // 在 3.8 循环里，确认轴线不存在时，把 AxisId 记下来
      missingAxisIds.Add(axisIdInWall);

      // 读取被删轴线的端点（来自墙线Name：AxisStart / AxisEnd）
      Point3d axS, axE;
      bool hasS = TryParsePointFromName(wn, "AxisStart", out axS);
      bool hasE = TryParsePointFromName(wn, "AxisEnd",   out axE);

      if (hasS)
      {
        foreach (var candId in QueryNearEndpoint(endpointIndex, axS, grid))
        {
          if (!liveAxisMap.TryGetValue(candId, out var candObj) || candObj == null) continue;
          var cc = candObj.Geometry as Curve;
          if (cc == null) continue;
          if (Math.Min(axS.DistanceTo(cc.PointAtStart), axS.DistanceTo(cc.PointAtEnd)) <= connectTol)
            {
             connectedFromDeletedSet.Add(candId);
            }
        }
      }
      if (hasE)
      {
        foreach (var candId in QueryNearEndpoint(endpointIndex, axE, grid))
        {
          if (!liveAxisMap.TryGetValue(candId, out var candObj) || candObj == null) continue;
          var cc = candObj.Geometry as Curve;
          if (cc == null) continue;
          if (Math.Min(axE.DistanceTo(cc.PointAtStart), axE.DistanceTo(cc.PointAtEnd)) <= connectTol)
            {
             connectedFromDeletedSet.Add(candId);
            }
        }
      }

      // ★ 孤儿墙线当场清理掉（否则永远脏）
      EnsureUnlocked(obj.Attributes.LayerIndex);
      doc.Objects.Delete(obj, true);
    }
  }

  // 恢复图层锁
  foreach (var kv in lockBackup)
  {
    int li = kv.Key;
    var ly = doc.Layers[li];
    if (ly == null) continue;
    ly.IsLocked = kv.Value;
    doc.Layers.Modify(ly, li, true);
  }

  // ★ 3.8补刀：把“被删除轴线”的封口线也清理掉
  if (missingAxisIds.Count > 0)
 {
  try
  {
    int removedOrphanCaps = DeleteWallCapsByAxisIds(doc, missingAxisIds.ToList(), deleteOrphanCaps: true);
  }
  catch { }

  // ★ 新增：把“被删除轴线”的墙体填充（WallHatch）也清理掉
  try
  {
    int removedOrphanHatches = DeleteWallHatchesByAxisIds(doc, missingAxisIds.ToList(), deleteOrphanHatches: true);
  }
  catch { }
 }



  // ★ 若本轮没有任何 dirty 轴线：不进入后续“Name同步/交点筛选/Split重建”
  //   - missingAxisIds / orphanWallCurvesDeleted 代表删轴线导致的孤儿清理；这部分前面已处理完
    if (dirtyAxisSet.Count == 0 
    && connectedAxisSet.Count == 0
    && connectedFromDeletedSet.Count == 0)
 {
  if (missingAxisIds.Count == 0 && orphanWallCurvesDeleted == 0)
    RhinoApp.WriteLine("[Refresh] 本轮未检测到轴线变化。");

  doc.Views.Redraw();
  return;
 }



  // ==================== 3.9：合并本轮要处理轴线集合 ====================
  
  foreach (var id in connectedAxisSet) dirtyAxisSet.Add(id);
  foreach (var id in connectedFromDeletedSet) dirtyAxisSet.Add(id);

  var dirtyAxisIds = new List<Guid>(dirtyAxisSet);
  
  // ✅ 先 simplify / join：会把被 join 吞掉的轴线 Guid 通过 out 返回，并且把 dirtyAxisIds 回写成“仍存在”的轴线
  List<Guid> removedByJoin;
  SimplifyAxesInPlace(doc, dirtyAxisIds, tol, out removedByJoin);

  // 再保险：只保留仍存在的轴线（避免后续把“找不到的轴线”当成被删轴线去扩散处理）
  dirtyAxisIds.RemoveAll(id => doc.Objects.Find(id) == null);

  // 如果 join 后本轮没有可处理轴线：只清理 removedByJoin 的墙线/保温线/cap，然后结束
  if (dirtyAxisIds.Count == 0)
 {
  if (removedByJoin != null && removedByJoin.Count > 0)
  {
    try { DeleteWallCapsByAxisIds(doc, removedByJoin, deleteOrphanCaps: true); } catch { }
    try { DeleteAllWallsAndInsulationForAxes(doc, removedByJoin); } catch { }
    try { DeleteWallHatchesByAxisIds(doc, removedByJoin, deleteOrphanHatches: true); } catch { }

    
  }

  doc.Views.Redraw();
  return;
 }

  // 3）把这些轴线的 Name 同步到“真实数据”（AxisId / Start / End / Axistype）
  foreach (var id in dirtyAxisIds)
 {
  var obj = doc.Objects.Find(id) as CurveObject;
  if (obj == null) continue;

  var crv = obj.Geometry as Curve;
  if (crv == null || !crv.IsValid) continue;

  var attr = obj.Attributes;
  string oldName = attr.Name ?? "";

  string newName = BuildSyncedWallaxisName(oldName, obj.Id, crv);
  if (newName != oldName)
  {
    attr.Name = newName;
    doc.Objects.ModifyAttributes(obj, attr, true);
  }
 }

  // 5）把 dirtyAxisIds 当作 “newAxisIdsThisRound”，走你现成的交点处理模块
  var axisToReplace = CollectIntersectingAxesForNewAxes(doc, axisLayerIndex, dirtyAxisIds);
  if (axisToReplace == null || axisToReplace.Count == 0)
 {
  RhinoApp.WriteLine("[Refresh] 交点筛选结果为空，取消后续重建。");
  doc.Views.Redraw();
  return;
 }

  // 清空待封口列表
  if (RTZ3State.axistobedeal != null)
  RTZ3State.axistobedeal.Clear();

  // ★ axisToDelete：用于“删墙线/保温线/cap”的 AxisId（包含 join 吞掉的旧轴线 Guid）
  var axisToDelete = new List<Guid>(axisToReplace);
  if (removedByJoin != null && removedByJoin.Count > 0)
 {
  foreach (var id in removedByJoin)
    if (!axisToDelete.Contains(id))
      axisToDelete.Add(id);
 }

  // 6）先清理旧 cap：
  //   - 对 axisToReplace：只精确删（deleteOrphanCaps=false）
  //   - 对 removedByJoin：顺带删孤儿（deleteOrphanCaps=true）
  try
 {
  int removedCaps = DeleteWallCapsByAxisIds(doc, axisToReplace, deleteOrphanCaps: false);
 }
 catch { }

 if (removedByJoin != null && removedByJoin.Count > 0)
  {
  try { DeleteWallCapsByAxisIds(doc, removedByJoin, deleteOrphanCaps: true); } catch { }
  }

  // 7）删旧墙线/保温线（用 axisToDelete，避免 join 吞掉的旧轴线留下孤儿墙线）
  DeleteAllWallsAndInsulationForAxes(doc, axisToDelete);

  // ★ Split 前再次过滤：只能喂“仍存在”的轴线 id（否则会触发你说的“扩散删很多关联轴线”）
  axisToReplace.RemoveAll(id => doc.Objects.Find(id) == null);
  dirtyAxisIds.RemoveAll(id => doc.Objects.Find(id) == null);

  int refreshedAxisCount = dirtyAxisIds.Count; // ★ 本轮成功刷新的轴线数量

  // —— 下面保持你原来的 SplitAxesAndRebuildWalls 调用不变 ——
  // SplitAxesAndRebuildWalls(...);


  // 8）打断轴线并重建
  SplitAxesAndRebuildWalls(
    doc,
    axisToReplace,
    dirtyAxisIds,
    wallLayerIndex,
    wallGlasLayerIndex,
    wallStudLayerIndex,
    axisLayerIndex,
    insulLayerIndex);

  // 9）封口
  try
  {
    if (!RTZ3State.SuppressCapsOnce)
    {
      ApplyWallChamferForAxesToBeDeal(doc, parentId);
      ApplyWallCapsForAxesToBeDeal(doc);
      ApplyWallBigFallbackForOrphanEnds(doc, parentId);
      ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
      }
    else
    {
      RTZ3State.SuppressCapsOnce = false;
    }
  }
  catch (Exception ex)
  {
   
  }
  doc.Views.Redraw();
  RhinoApp.WriteLine("本轮共刷新 {0} 条轴线。", refreshedAxisCount);
 }
  finally
 {
  ForceRelockWallRelatedLayers();
 }

}

void RunRedrawWallsForSelection(RhinoDoc doc, ThicknessPanel panel = null, List<Guid> axisIdsPreferred = null, bool silent = false)
{
  if (doc == null) return;
double tol = doc.ModelAbsoluteTolerance;

  // 端点/近似判定宽松一点（跟 Refresh 一致）
  double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  double connectTol = Math.Max(tol, 1.0 * mm);

  // 0）确定当前分区 parentId（沿用你 Refresh 的写法）
  Guid parentId = Guid.Empty;
  int curLi = doc.Layers.CurrentLayerIndex;
  if (curLi >= 0 && curLi < doc.Layers.Count)
  {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
      parentId = curLay.ParentLayerId;
      if (parentId == Guid.Empty)
        parentId = curLay.Id; // 顶层分区
    }
  }

  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;


  // 1）找本分区下的 PB-Wall-Axis
  int axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLayerIndex < 0)
  {
    RhinoApp.WriteLine("[RedrawWalls] 当前分区未找到 PB-Wall-Axis。");
    return;
  }

  // ★ 若轴线图层锁定，临时解锁（否则删/重建会失败）
  bool axisLayerWasLocked = false;
  var axisLayer = doc.Layers[axisLayerIndex];
  if (axisLayer != null && axisLayer.IsLocked)
  {
    axisLayerWasLocked = true;
    axisLayer.IsLocked = false;
    doc.Layers.Modify(axisLayer, axisLayerIndex, true);
  }

  try
  {
        // 2）确定要重绘的轴线：
    //   - silent=true 且 axisIdsPreferred 非空：后台模式，直接用传入的轴线列表（不做任何交互）
    //   - 否则：沿用原 Wall2D 交互方式（GetObject，支持预选）
    var selectedAxisIds = new List<Guid>();

    if (silent && axisIdsPreferred != null && axisIdsPreferred.Count > 0)
    {
      foreach (var id in axisIdsPreferred)
      {
        if (id == Guid.Empty) continue;

        var co = doc.Objects.Find(id) as CurveObject;
        if (co == null) continue;

        // 只接受 PB-Wall-Axis 图层上的轴线（避免跨分区误处理）
        if (co.Attributes.LayerIndex != axisLayerIndex)
          continue;

        // 你要求：Name 为空的轴线当空气，直接忽略
        string n = co.Attributes.Name ?? "";
        if (string.IsNullOrWhiteSpace(n))
          continue;

        // 只处理 Wallaxis
        if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
          continue;

        selectedAxisIds.Add(id);
      }

      // ★ 后台模式：如果最终没有任何可用轴线，直接跳过（不要弹出选择提示）
      if (selectedAxisIds.Count == 0)
      {
        RhinoApp.WriteLine("[RedrawWalls] 本轮没有可重绘的新增轴线，跳过。");
        return;
      }
    }
    else
    {
      // 交互模式：让用户选择要重绘的轴线
      var go = new GetObject();
      go.SetCommandPrompt("选择要重绘墙体的轴线（支持预选）");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(true, true);
      go.DeselectAllBeforePostSelect = false;

      go.GetMultiple(1, 0);
      if (go.CommandResult() != Rhino.Commands.Result.Success)
        return;

      for (int i = 0; i < go.ObjectCount; i++)
      {
        Guid id = go.Object(i).ObjectId;
        var co = doc.Objects.Find(id) as CurveObject;
        if (co == null) continue;

        // 只接受 PB-Wall-Axis 图层上的轴线（避免用户误选）
        if (co.Attributes.LayerIndex != axisLayerIndex)
          continue;

        // 你要求：Name 为空的轴线当空气，直接忽略
        string n = co.Attributes.Name ?? "";
        if (string.IsNullOrWhiteSpace(n))
          continue;

        // 只处理 Wallaxis
        if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
          continue;

        selectedAxisIds.Add(id);
      }

      if (selectedAxisIds.Count == 0)
      {
        RhinoApp.WriteLine("[RedrawWalls] 未选到可重绘的轴线");
        return;
      }
    }

// ✅ ★ 新增：先 simplify（平滑合并 + Normalize），再同步 Name
    List<Guid> removedByJoin;
    SimplifyAxesInPlace(doc, selectedAxisIds, tol, out removedByJoin);

    // 再保险：只保留仍存在的轴线
    selectedAxisIds.RemoveAll(id => doc.Objects.Find(id) == null);

    // 3）先把这些轴线的 Name 同步到真实数据（AxisId / Start / End / Axistype）
    foreach (var id in selectedAxisIds)
    {
      var obj = doc.Objects.Find(id) as CurveObject;
      if (obj == null) continue;

      var crv = obj.Geometry as Curve;
      if (crv == null || !crv.IsValid) continue;

      var attr = obj.Attributes;
      string oldName = attr.Name ?? "";

      // 这里用你现成的 BuildSyncedWallaxisName（只更新 AxisId/Start/End/Axistype，尽量保留其它参数）
      string newName = BuildSyncedWallaxisName(oldName, obj.Id, crv);

      if (newName != oldName)
      {
        attr.Name = newName;
        doc.Objects.ModifyAttributes(obj, attr, true);
      }
    }

    // 4）准备层索引（局部变量用新名字，避免你之前重复定义报错）
    int wLi  = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
    int wgLi = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
    int wsLi = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
    int insLi= FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");

    // 5）把 selectedAxisIds 当作 newAxisIdsThisRound，走你现成的交点筛选
    var axisToReplace = CollectIntersectingAxesForNewAxes(doc, axisLayerIndex, selectedAxisIds);

    if (axisToReplace == null || axisToReplace.Count == 0)
    {
      RhinoApp.WriteLine("[RedrawWalls] 交点筛选为空，取消重绘。");
      return;
    }

    // 清空待封口列表，避免残留
    if (RTZ3State.axistobedeal != null)
      RTZ3State.axistobedeal.Clear();

    // 6）先清理旧 cap（靠 AxisId 精确匹配）
    try
    {
      int removedCaps = DeleteWallCapsByAxisIds(doc, axisToReplace, deleteOrphanCaps: false);
      RhinoApp.WriteLine("[RedrawWalls][Cap] 预清理旧cap：删除 {0} 条", removedCaps);
    }
    catch { }

    var axisToDelete = new List<Guid>(axisToReplace);
 if (removedByJoin != null && removedByJoin.Count > 0)
 {
  foreach (var id in removedByJoin)
    if (!axisToDelete.Contains(id))
      axisToDelete.Add(id);
 }

 // 删 cap：axisToReplace 精确删；removedByJoin 顺带删孤儿
 try { DeleteWallCapsByAxisIds(doc, axisToReplace, deleteOrphanCaps: false); } catch { }
 if (removedByJoin != null && removedByJoin.Count > 0)
 {
  try { DeleteWallCapsByAxisIds(doc, removedByJoin, deleteOrphanCaps: true); } catch { }
 }
    
    // 7）删旧墙线/保温线
    DeleteAllWallsAndInsulationForAxes(doc, axisToReplace);

    // 8）打断并重建（完全复用你的管线）
    SplitAxesAndRebuildWalls(
      doc,
      axisToReplace,
      selectedAxisIds,
      wLi,
      wgLi,
      wsLi,
      axisLayerIndex,
      insLi);

    // 9）重建完成后封口
    try
    {
      if (!RTZ3State.SuppressCapsOnce)
      {
        ApplyWallChamferForAxesToBeDeal(doc, parentId);
        ApplyWallCapsForAxesToBeDeal(doc);
        ApplyWallBigFallbackForOrphanEnds(doc, parentId);
        ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
      }
      else
      {
        RTZ3State.SuppressCapsOnce = false;
      }

    }
    catch (Exception ex)
    {
      RhinoApp.WriteLine("[RedrawWalls][Cap] 封口异常：{0}", ex.Message);
    }

    RhinoApp.WriteLine("[RedrawWalls] 完成：用户选定 {0} 条轴线，参与重建 {1} 条。",
                       selectedAxisIds.Count, axisToReplace.Count);

    doc.Views.Redraw();
  }
  finally
  {
    // 恢复轴线层锁定状态
    if (axisLayerWasLocked && axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
    {
      var al = doc.Layers[axisLayerIndex];
      if (al != null)
      {
        al.IsLocked = true;
        doc.Layers.Modify(al, axisLayerIndex, true);
      }
    }
    // ===== ✅ 新增：命令结束后，强制锁回墙相关图层（Wall/Glas/Stud/Insul + Hatch）=====
    try
    {
        int wl = -1, wg = -1, ws = -1, ins = -1;
        LockExistingWallLayers(doc, parentId, ref wl, ref wg, ref ws, ref ins);
        ForceLockWallHatchLayer(doc, parentId);
    }
    catch { }
  }
}

void RunRedrawAllForCurrentParent(RhinoDoc doc, ThicknessPanel panel = null)
{
  if (doc == null) return;
double tol = doc.ModelAbsoluteTolerance;


  // 0）确定当前分区 parentId（和 Refresh 同一套）
  Guid parentId = Guid.Empty;
  int curLi = doc.Layers.CurrentLayerIndex;
  if (curLi >= 0 && curLi < doc.Layers.Count)
  {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
      parentId = curLay.ParentLayerId;
      if (parentId == Guid.Empty) parentId = curLay.Id;
    }
  }

  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;


  // 1）找本分区 PB-Wall-Axis
  int axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLayerIndex < 0)
  {
    RhinoApp.WriteLine("[RedrawAll] 当前分区未找到 PB-Wall-Axis。");
    return;
  }

  var axisLayer = doc.Layers[axisLayerIndex];
  if (axisLayer == null)
  {
    RhinoApp.WriteLine("[RedrawAll] PB-Wall-Axis 图层异常。");
    return;
  }

  var axisObjs = doc.Objects.FindByLayer(axisLayer);
  if (axisObjs == null || axisObjs.Length == 0)
  {
    RhinoApp.WriteLine("[RedrawAll] PB-Wall-Axis 下没有轴线。");
    return;
  }

  // 2）准备层索引（给 SplitAxesAndRebuildWalls 用）
  int wallLayerIndex     = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
  int wallGlasLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
  int wallStudLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
  int insulLayerIndex    = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");

  // 3）收集“有效轴线”：只认 Name 非空 且 Wallaxis 开头
  var axisIds = new List<Guid>();

  foreach (var o in axisObjs)
  {
    var co = o as CurveObject;
    if (co == null) continue;

    var crv = co.Geometry as Curve;
    if (crv == null || !crv.IsValid) continue;

    string n = co.Attributes.Name ?? "";
    if (string.IsNullOrEmpty(n)) continue; // ✅ 你要求：Name 为空当空气
    if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase)) continue;

    axisIds.Add(co.Id);
  }

  if (axisIds.Count == 0)
  {
    RhinoApp.WriteLine("[RedrawAll] 没有可重绘的 Wallaxis 轴线。");
    return;
  }

  // ★ 若轴线层锁了：临时解锁（Split/重建会改轴线）
  bool axisLayerWasLocked = false;
  if (axisLayer.IsLocked)
  {
    axisLayerWasLocked = true;
    axisLayer.IsLocked = false;
    doc.Layers.Modify(axisLayer, axisLayerIndex, true);
  }

  try
  {
    
    List<Guid> removedByJoin;
 SimplifyAxesInPlace(doc, axisIds, tol, out removedByJoin);

 axisIds.RemoveAll(id => doc.Objects.Find(id) == null);

    // 4）同步这些轴线的 Name（AxisId / Start / End / Axistype）
    foreach (var id in axisIds)
    {
      var obj = doc.Objects.Find(id) as CurveObject;
      if (obj == null) continue;

      var crv = obj.Geometry as Curve;
      if (crv == null || !crv.IsValid) continue;

      var attr = obj.Attributes;
      string oldName = attr.Name ?? "";
      if (string.IsNullOrEmpty(oldName)) continue; // 再保险一次

      string newName = BuildSyncedWallaxisName(oldName, obj.Id, crv);
      if (newName != oldName)
      {
        attr.Name = newName;
        doc.Objects.ModifyAttributes(obj, attr, true);
      }
    }

    var axisToReplace = new List<Guid>(axisIds);

 var axisToDelete = new List<Guid>(axisToReplace);
 if (removedByJoin != null && removedByJoin.Count > 0)
 {
  foreach (var id in removedByJoin)
    if (!axisToDelete.Contains(id))
      axisToDelete.Add(id);
 }

 try { DeleteWallCapsByAxisIds(doc, axisToReplace, deleteOrphanCaps: true); } catch { }
 if (removedByJoin != null && removedByJoin.Count > 0)
 {
  try { DeleteWallCapsByAxisIds(doc, removedByJoin, deleteOrphanCaps: true); } catch { }
 }

    // 7）删旧墙线/保温线（你已有函数，支持锁层）
    DeleteAllWallsAndInsulationForAxes(doc, axisToReplace);

    // 8）打断轴线并重建墙线/保温线（复用你的管线）
    SplitAxesAndRebuildWalls(
      doc,
      axisToReplace,
      axisIds,                 // newAxisIdsThisRound：这里就是“全部轴线”
      wallLayerIndex,
      wallGlasLayerIndex,
      wallStudLayerIndex,
      axisLayerIndex,
      insulLayerIndex);

    // 9）重建完成后：按你的总逻辑封口
    try
    {
      if (!RTZ3State.SuppressCapsOnce)
      {
        ApplyWallChamferForAxesToBeDeal(doc, parentId);
        ApplyWallCapsForAxesToBeDeal(doc);
        ApplyWallBigFallbackForOrphanEnds(doc, parentId);
        ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
      }
      else
      {
        RTZ3State.SuppressCapsOnce = false;
      }

    }
    catch (Exception ex)
    {
      RhinoApp.WriteLine("[RedrawAll][Cap] 封口异常：{0}", ex.Message);
    }

    RhinoApp.WriteLine("[RedrawAll] 完成：重绘 {0} 条轴线。", axisIds.Count);
  }
  finally
  {
    // ★ 恢复轴线层锁
    if (axisLayerWasLocked && axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
    {
      var al = doc.Layers[axisLayerIndex];
      if (al != null)
      {
        al.IsLocked = true;
        doc.Layers.Modify(al, axisLayerIndex, true);
      }
    }
    try
    {
        int wl = -1, wg = -1, ws = -1, ins = -1;
        LockExistingWallLayers(doc, parentId, ref wl, ref wg, ref ws, ref ins);
        ForceLockWallHatchLayer(doc, parentId);
    }
        catch { }

  }

  doc.Views.Redraw();
}

void RunEditWallsMode(RhinoDoc doc, ThicknessPanel panel)
{
  if (doc == null || panel == null) return;
  if (panel.Cancelled) return;

  // 0) 当前分区 parentId
  Guid parentId = Guid.Empty;
  int curLi = doc.Layers.CurrentLayerIndex;
  if (curLi >= 0 && curLi < doc.Layers.Count)
  {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
      parentId = curLay.ParentLayerId;
      if (parentId == Guid.Empty) parentId = curLay.Id;
    }
  }

  double tol = doc.ModelAbsoluteTolerance;

  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;


  // 1) 找轴线层
  int axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLayerIndex < 0)
  {
    RhinoApp.WriteLine("[Edit] 当前分区未找到 PB-Wall-Axis");
    panel.SetWallGenerateMode(WallGenerateMode.None);
    return;
  }

  // 2) 选择一根轴线（不使用 CustomGeometryFilter：因为在你这个 RhinoCommon 版本里它是 method group，不能赋值）
  Rhino.DocObjects.ObjRef axisRef = null;
  Rhino.DocObjects.RhinoObject axisObj = null;
  Rhino.Geometry.Curve axisCrv = null;
  Rhino.Input.GetResult res;

  while (true)
 {
  var go = new Rhino.Input.Custom.GetObject();
  go.SetCommandPrompt("请选择轴线进行墙体编辑");
  go.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
  go.SubObjectSelect = false;
  go.EnablePreSelect(true, true);
  go.EnablePostSelect(true);

  res = go.Get();
  if (res != Rhino.Input.GetResult.Object || go.ObjectCount < 1)
  {
    panel.SetWallGenerateMode(WallGenerateMode.None);
    return;
  }

  axisRef = go.Object(0);
  axisObj = axisRef.Object();
  axisCrv = axisRef.Curve();

  if (axisObj == null || axisCrv == null)
  {
    Rhino.RhinoApp.WriteLine("[Edit] 请选择有效的轴线曲线。");
    continue;
  }

  // 图层必须是 PB-Wall-Axis
  if (axisObj.Attributes.LayerIndex != axisLayerIndex)
  {
    Rhino.RhinoApp.WriteLine("[Edit] 请选择本【父级】图层内的的【轴线】");
    continue;
  }

  // Name 必须是 Wallaxis|
  var n = axisObj.Attributes.Name ?? "";
  if (!n.StartsWith("Wallaxis|", StringComparison.OrdinalIgnoreCase))
  {
    Rhino.RhinoApp.WriteLine("[Edit] 选择必须为【轴线】。");
    continue;
  }

  break;
 }

  Guid axisId = axisObj.Id;

  

  // 3) 备份面板状态（用于取消恢复）
  var snapshot = panel.CaptureSnapshot();

  // 4) 解析轴线 name -> AxisInfo
  string oldName = axisObj.Attributes.Name ?? "";
  // 确保 name 至少含 AxisId/Axistype/Start/End（你已有同步函数）
  string syncedName = BuildSyncedWallaxisName(oldName, axisId, axisCrv);
  if (syncedName != oldName)
  {
    var attr = axisObj.Attributes.Duplicate();
    attr.Name = syncedName;
    doc.Objects.ModifyAttributes(axisObj, attr, true);
    oldName = syncedName;
  }

  var info = new AxisInfo();
  info.AxisId = axisId;
  info.AxisCurve = axisCrv.DuplicateCurve();
  FillAxisInfoFromName(oldName, info);

  // 5) 用轴线参数刷新面板（关键：把 name 中的参数同步到 UI）
  // name 里存的是 OffsetL/OffsetR（用于生成墙线），而面板输入是 a/b，需要反推
  double a = info.OffsetL;
  double b = info.OffsetR;

  double ai = info.InsulLeftOffset;
  double bi = info.InsulRightOffset;
  double aiForWall = info.InsulLeftOn ? ai : 0.0;
  double biForWall = info.InsulRightOn ? bi : 0.0;

  if (info.UseInsulLeftAsOutline && !info.UseInsulRightAsOutline)
  {
    a = info.OffsetL + aiForWall;
    b = info.OffsetR - aiForWall;
  }
  else if (info.UseInsulRightAsOutline && !info.UseInsulLeftAsOutline)
  {
    a = info.OffsetL - biForWall;
    b = info.OffsetR + biForWall;
  }

  // 构造一个新 snapshot 应用到面板（只替换墙体相关项）
  var s2 = snapshot;
  s2.L = a;
  s2.R = b;
  s2.InsLOn = info.InsulLeftOn;
  s2.InsROn = info.InsulRightOn;
  s2.InsL = ai;
  s2.InsR = bi;
  s2.OutlineL = info.UseInsulLeftAsOutline;
  s2.OutlineR = info.UseInsulRightAsOutline;
  s2.WallKind = info.WallType;

  panel.ApplySnapshot(s2);

  // 6) 临时隐藏旧墙线/保温线/封口线（确认前可恢复）
  var hidden = new Dictionary<Guid, bool>(); // id -> oldVisible

  void HideObj(Guid id)
  {
    var ro = doc.Objects.FindId(id);
    if (ro == null) return;
    bool wasVis = ro.Attributes.Visible;
    if (hidden.ContainsKey(id)) return;
    hidden[id] = wasVis;

    if (!wasVis) return;
    var at = ro.Attributes.Duplicate();
    at.Visible = false;
    doc.Objects.ModifyAttributes(ro, at, true);
  }

  // ★ EditWalls：为了能 show/delete，临时解锁相关图层
  var lockBackup = new Dictionary<int, bool>();

    void EnsureUnlocked(int li)
 {
  if (li < 0 || li >= doc.Layers.Count) return;
  if (lockBackup.ContainsKey(li)) return;
  var ly = doc.Layers[li];
  if (ly == null) return;

  lockBackup[li] = ly.IsLocked;
  if (ly.IsLocked)
  {
    ly.IsLocked = false;
    doc.Layers.Modify(ly, li, true);
  }
 }

    void RestoreLayerLocks()
 {
  foreach (var kv in lockBackup)
  {
    int li = kv.Key;
    bool wasLocked = kv.Value;
    if (!wasLocked) continue;
    if (li < 0 || li >= doc.Layers.Count) continue;
    var ly = doc.Layers[li];
    if (ly == null) continue;
    ly.IsLocked = true;
    doc.Layers.Modify(ly, li, true);
  }
 }

    void SetObjVisible(Guid id, bool v)
 {
  var ro = doc.Objects.FindId(id);
  if (ro == null) return;
  EnsureUnlocked(ro.Attributes.LayerIndex);

  var at = ro.Attributes.Duplicate();
  at.Visible = v;
  doc.Objects.ModifyAttributes(ro, at, true);
 }

    void DeleteObj(Guid id)
 {
  var ro = doc.Objects.FindId(id);
  if (ro == null) return;
  EnsureUnlocked(ro.Attributes.LayerIndex);
  doc.Objects.Delete(ro, true);
 }


  var oes = NewEnumeratorAllCurves();
  foreach (var ro in doc.Objects.GetObjectList(oes))
 {
  if (ro == null) continue;
  if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId)) continue;
  var nm = ro.Attributes.Name ?? "";
  if (string.IsNullOrEmpty(nm)) continue;

  string idStr = axisId.ToString();

  if ((nm.StartsWith("Wall|AxisId=", StringComparison.OrdinalIgnoreCase) ||
       nm.StartsWith("Wallinside|AxisId=", StringComparison.OrdinalIgnoreCase)) &&
      nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0)
    HideObj(ro.Id);
  else if (nm.StartsWith("Insulation|AxisId=", StringComparison.OrdinalIgnoreCase) && nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0)
    HideObj(ro.Id);
  else if (nm.StartsWith("wallcap|", StringComparison.OrdinalIgnoreCase) && nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0)
    HideObj(ro.Id);
 }

  // 7) 进入动态预览：复用你已有 conduit（会自动跟随面板变化）
  var preview = new RTZ3SingleToWallPreviewConduit(doc, panel);
  preview.AxisCurves.Clear();
  preview.AxisCurves.Add(axisCrv.DuplicateCurve());
  preview.Enabled = true;
  doc.Views.Redraw();

  bool confirmed = false;
  try
  {
    var gs = new Rhino.Input.Custom.GetString();
    gs.SetCommandPrompt("编辑墙体：修改面板数据预览；回车确认；Esc取消");
    gs.AcceptNothing(true);

    var r = gs.Get();
    if (r == Rhino.Input.GetResult.Nothing)
      confirmed = true;
    else
      confirmed = false;
  }
  finally
  {
    preview.Enabled = false;
    doc.Views.Redraw();
  }

  // 8) 取消：恢复原样
    if (!confirmed)
 {
  // 取消：恢复可见性（解锁后才能改）
  foreach (var kv in hidden)
    SetObjVisible(kv.Key, kv.Value);

  RestoreLayerLocks();
  doc.Views.Redraw();
  return;
 }

    // confirm：先把隐藏的都 show 出来，再删除（解锁后才能 show）
    foreach (var kv in hidden)
  SetObjVisible(kv.Key, true);

    foreach (var kv in hidden)
  DeleteObj(kv.Key);

    RestoreLayerLocks();
    doc.Views.Redraw();

  // 9) 确认：把面板新数据写回轴线 name（替换老数据）

  double A = panel.LeftValue;
  double B = panel.RightValue;
  double Ai = panel.InsulLeftValue;
  double Bi = panel.InsulRightValue;

  double AiForWall = panel.InsulLeftOn ? Ai : 0.0;
  double BiForWall = panel.InsulRightOn ? Bi : 0.0;

  double newL, newR;
  ComputeWallOffsets(A, B, AiForWall, BiForWall,
    panel.UseInsulLeftAsOutline, panel.UseInsulRightAsOutline,
    out newL, out newR);

  string newName2 = BuildSyncedWallaxisName(oldName, axisId, axisCrv);
  newName2 = SetOrAppendTag(newName2, "L", newL.ToString());
  newName2 = SetOrAppendTag(newName2, "R", newR.ToString());
  newName2 = SetOrAppendTag(newName2, "Walltype", GetWallKindTag(panel.CurrentWallKind));

  newName2 = SetOrAppendTag(newName2, "InsulL", Ai.ToString());
  newName2 = SetOrAppendTag(newName2, "InsulR", Bi.ToString());
  newName2 = SetOrAppendTag(newName2, "InsulLOn", panel.InsulLeftOn ? "1" : "0");
  newName2 = SetOrAppendTag(newName2, "InsulROn", panel.InsulRightOn ? "1" : "0");
  newName2 = SetOrAppendTag(newName2, "UseInsulLAsOutline", panel.UseInsulLeftAsOutline ? "1" : "0");
  newName2 = SetOrAppendTag(newName2, "UseInsulRAsOutline", panel.UseInsulRightAsOutline ? "1" : "0");

  {
    var at = axisObj.Attributes.Duplicate();
    at.Name = newName2;
    doc.Objects.ModifyAttributes(axisObj, at, true);
  }

  // 10) 删除隐藏的旧墙线/保温线/封口线，并走你已有“重建管线”
  //     等价于：把这根轴线作为 dirty/newAxisIds 送入现有流程
  var selectedAxisIds = new List<Guid> { axisId };

  int wallLayerIndex      = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
  int wallGlasLayerIndex  = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
  int wallStudLayerIndex  = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
  int insulLayerIndex     = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");

  var axisToReplace = CollectIntersectingAxesForNewAxes(doc, axisLayerIndex, selectedAxisIds);

  if (RTZ3State.axistobedeal != null) RTZ3State.axistobedeal.Clear();

  DeleteWallCapsByAxisIds(doc, axisToReplace, false);
  DeleteAllWallsAndInsulationForAxes(doc, axisToReplace);

  SplitAxesAndRebuildWalls(doc,
    axisToReplace,
    selectedAxisIds,
    wallLayerIndex,
    wallGlasLayerIndex,
    wallStudLayerIndex,
    axisLayerIndex,
    insulLayerIndex
    );

  // 11) 封口（沿用你的机制）
  if (!RTZ3State.SuppressCapsOnce)
  {
    ApplyWallChamferForAxesToBeDeal(doc, parentId);
    ApplyWallCapsForAxesToBeDeal(doc);
    ApplyWallBigFallbackForOrphanEnds(doc, parentId);
    ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
  }
  else
  {
    RTZ3State.SuppressCapsOnce = false;
  }
  // ★ 结束墙体编辑：强制锁回墙相关图层（不受用户手动解锁影响）
  LockExistingWallLayers(doc, parentId,
  ref wallLayerIndex, ref wallGlasLayerIndex, ref wallStudLayerIndex, ref insulLayerIndex);

  // ★ 墙体填充层也强制锁回（你已有这个函数）
  ForceLockWallHatchLayer(doc, parentId);


  panel.SetWallGenerateMode(WallGenerateMode.None);
  doc.Views.Redraw();
}

void RunFormatBrushMode(RhinoDoc doc, ThicknessPanel panel)
{
  if (doc == null || panel == null) return;
  if (panel.Cancelled) return;


  // 进入格式刷模式（用于按钮互斥/提示）
  panel.SetWallGenerateMode(WallGenerateMode.FormatBrush);

  var snapshot = panel.CaptureSnapshot();
  double tol = doc.ModelAbsoluteTolerance;

  // -------------------------
  // 0) 本命令用到的临时状态：隐藏/解锁备份
  // -------------------------
  var hiddenVis = new Dictionary<Guid, bool>();     // objId -> oldVisible
  var layerLockBackup = new Dictionary<int, bool>(); // layerIndex -> oldLocked

  void EnsureUnlocked(int li)
  {
    if (li < 0 || li >= doc.Layers.Count) return;
    if (layerLockBackup.ContainsKey(li)) return;

    var ly = doc.Layers[li];
    if (ly == null) return;

    layerLockBackup[li] = ly.IsLocked;
    if (ly.IsLocked)
    {
      ly.IsLocked = false;
      doc.Layers.Modify(ly, li, true);
    }
  }

  void RestoreLayerLocks()
  {
    foreach (var kv in layerLockBackup)
    {
      int li = kv.Key;
      bool wasLocked = kv.Value;
      if (!wasLocked) continue;
      if (li < 0 || li >= doc.Layers.Count) continue;

      var ly = doc.Layers[li];
      if (ly == null) continue;

      ly.IsLocked = true;
      doc.Layers.Modify(ly, li, true);
    }
    layerLockBackup.Clear();
  }

  void SetObjVisible(Guid id, bool v)
  {
    var ro = doc.Objects.FindId(id);
    if (ro == null) return;

    EnsureUnlocked(ro.Attributes.LayerIndex);

    var at = ro.Attributes.Duplicate();
    at.Visible = v;
    doc.Objects.ModifyAttributes(ro, at, true);
  }

  void HideRelatedObjectsForAxis(Guid axisId, Guid parentId)
  {
    string idStr = axisId.ToString();
    var oes = NewEnumeratorAllCurves();

    foreach (var ro in doc.Objects.GetObjectList(oes))
    {
      if (ro == null) continue;

      // 只处理当前分区（parentId 下）的对象
      if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId))
        continue;

      string nm = ro.Attributes.Name ?? "";
      bool hit =
        ((nm.StartsWith("Wall|AxisId=", StringComparison.OrdinalIgnoreCase) ||
          nm.StartsWith("Wallinside|AxisId=", StringComparison.OrdinalIgnoreCase)) &&
         nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0) ||
        (nm.StartsWith("Insulation|AxisId=", StringComparison.OrdinalIgnoreCase) &&
         nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0) ||
        (nm.StartsWith("wallcap|", StringComparison.OrdinalIgnoreCase) &&
         nm.IndexOf(idStr, StringComparison.OrdinalIgnoreCase) >= 0);

      if (!hit) continue;

      if (!hiddenVis.ContainsKey(ro.Id))
        hiddenVis[ro.Id] = ro.Attributes.Visible;

      EnsureUnlocked(ro.Attributes.LayerIndex);
      SetObjVisible(ro.Id, false);
    }
  }

  void RestoreHiddenObjects()
  {
    foreach (var kv in hiddenVis)
      SetObjVisible(kv.Key, kv.Value);
    hiddenVis.Clear();
  }

  bool IsWallAxisObj(RhinoObject ro)
  {
    if (ro == null) return false;
    if (!(ro is CurveObject)) return false;
    int li = ro.Attributes.LayerIndex;
    if (li < 0 || li >= doc.Layers.Count) return false;

    var lay = doc.Layers[li];
    if (lay == null) return false;

    // 必须在 PB-Wall-Axis 图层
    if (!lay.Name.Equals("PB-Wall-Axis", StringComparison.OrdinalIgnoreCase))
      return false;

    string nm = ro.Attributes.Name ?? "";
    return nm.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase);
  }

  // 读取 cap（兼容 capston/capedon 与 capstarton/capendon）
  int GetCapStart01(string name)
  {
    int v = GetTag01(name, "capstarton", -1);
    if (v >= 0) return v;
    return GetTag01(name, "capston", 0);
  }
  int GetCapEnd01(string name)
  {
    int v = GetTag01(name, "capendon", -1);
    if (v >= 0) return v;
    return GetTag01(name, "capedon", 0);
  }

  // 将样本参数写入目标轴线 Name（保留 AxisId/端点/类型 的同步）
  void WriteSampleIntoAxisName(CurveObject targetAxisObj, AxisInfo sampleInfo, int capStOn, int capEdOn)
  {
    if (targetAxisObj == null) return;
    var crv = targetAxisObj.Geometry as Curve;
    if (crv == null) return;

    string oldName = targetAxisObj.Attributes.Name ?? "Wallaxis";
    string name = BuildSyncedWallaxisName(oldName, targetAxisObj.Id, crv);

    // L/R 写“存储的 offset”（不是面板 A/B）
    name = SetOrAppendTag(name, "L", sampleInfo.OffsetL.ToString("0.###", CultureInfo.InvariantCulture));
    name = SetOrAppendTag(name, "R", sampleInfo.OffsetR.ToString("0.###", CultureInfo.InvariantCulture));

    name = SetOrAppendTag(name, "Walltype", GetWallKindTag(sampleInfo.WallType));

    name = SetOrAppendTag(name, "InsulL", sampleInfo.InsulLeftOffset.ToString("0.###", CultureInfo.InvariantCulture));
    name = SetOrAppendTag(name, "InsulR", sampleInfo.InsulRightOffset.ToString("0.###", CultureInfo.InvariantCulture));
    name = SetOrReplaceTag01(name, "InsulLOn", sampleInfo.InsulLeftOn ? 1 : 0);
    name = SetOrReplaceTag01(name, "InsulROn", sampleInfo.InsulRightOn ? 1 : 0);

    name = SetOrReplaceTag01(name, "UseInsulLAsOutline", sampleInfo.UseInsulLeftAsOutline ? 1 : 0);
    name = SetOrReplaceTag01(name, "UseInsulRAsOutline", sampleInfo.UseInsulRightAsOutline ? 1 : 0);

    // cap：尽量沿用目标自身的 key 习惯（有长用长，否则用短）
    bool hasLong = name.IndexOf("|capstarton=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("|capendon=",   StringComparison.OrdinalIgnoreCase) >= 0;

    if (hasLong)
    {
      name = SetOrReplaceTag01(name, "capstarton", capStOn);
      name = SetOrReplaceTag01(name, "capendon",   capEdOn);
    }
    else
    {
      name = SetOrReplaceTag01(name, "capston", capStOn);
      name = SetOrReplaceTag01(name, "capedon", capEdOn);
    }

    ModifyObjectName(doc, targetAxisObj, name);
  }

    // -------------------------
    // 1) 预选支持（按你的新规则）
    //   - 预选 >= 2 根轴线：开局全部取消预选，走“正常提示选样本”流程
    //   - 预选 = 1  根轴线：允许当作样本，点按钮立刻开工
    //   - 预选 = 0 ：正常提示选样本
    // -------------------------
    Guid sampleAxisId = Guid.Empty;
    CurveObject sampleAxisObj = null;

    var targetAxisIds = new List<Guid>();
    var targetAxisCurves = new List<Curve>();

    int axisLayerIndex = -1;
    Guid parentId = Guid.Empty;

    var preSel = doc.Objects.GetSelectedObjects(false, false);
    var preAxes = new List<CurveObject>();
    if (preSel != null)
  {
    foreach (var ro in preSel)
   {
    if (!IsWallAxisObj(ro)) continue;
    preAxes.Add((CurveObject)ro);
   }
  }

    // ★新规则：如果预选了两根（或更多）轴线 → 直接取消全部预选，强制走“请选择样本墙轴线”
    if (preAxes.Count >= 2)
  {
  doc.Objects.UnselectAll();
  doc.Views.Redraw();

  preAxes.Clear();
  // 注意：这里不要设置 sampleAxisObj，让后面逻辑进入“手动选样本”
  }
    else if (preAxes.Count == 1)
  {
  // 预选 1 根：允许当样本，点按钮立刻工作
  sampleAxisObj = preAxes[0];
  sampleAxisId = sampleAxisObj.Id;

  axisLayerIndex = sampleAxisObj.Attributes.LayerIndex;
  var axisLayer = doc.Layers[axisLayerIndex];
  if (axisLayer != null)
  {
    parentId = axisLayer.ParentLayerId;
    if (parentId == Guid.Empty) parentId = axisLayer.Id;
  }

  // ✅关键：消费掉预选，避免后续 GetObject 因预选反复命中导致死循环
  doc.Objects.UnselectAll();
  doc.Views.Redraw();
  }


  // -------------------------
  // 2) 若没预选样本：让用户选样本（只允许 post-select）
  // -------------------------
  try
  {
    if (sampleAxisObj == null)
    {
      // 从当前图层推一个 parentId / axisLayerIndex（兜底）
      int curLi = doc.Layers.CurrentLayerIndex;
      if (curLi >= 0 && curLi < doc.Layers.Count)
      {
        var curLay = doc.Layers[curLi];
        if (curLay != null)
        {
          parentId = curLay.ParentLayerId;
          if (parentId == Guid.Empty) parentId = curLay.Id;
        }
      }

      if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
      return;


      axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
      if (axisLayerIndex < 0)
      {
        RhinoApp.WriteLine("当前分区未找到 PB-Wall-Axis 图层。");
        panel.ApplySnapshot(snapshot);
        panel.SetWallGenerateMode(WallGenerateMode.None);
        return;
      }

      var goS = new Rhino.Input.Custom.GetObject();
      goS.SetCommandPrompt("格式刷：请选择【样本】墙体轴线（PB-Wall-Axis）");
      goS.GeometryFilter = ObjectType.Curve;
      goS.SubObjectSelect = false;
      goS.EnablePreSelect(false, true);
      goS.DeselectAllBeforePostSelect = true;

      var gr = goS.Get();
      if (gr == GetResult.Cancel) throw new OperationCanceledException();
      if (gr != GetResult.Object) throw new OperationCanceledException();

      var ro = goS.Object(0).Object() as CurveObject;
      if (ro == null || !IsWallAxisObj(ro) || ro.Attributes.LayerIndex != axisLayerIndex)
      {
        RhinoApp.WriteLine("格式刷：请选择【本父级】图层内的【轴线】");
        throw new OperationCanceledException();
      }

      sampleAxisObj = ro;
      sampleAxisId = ro.Id;

      // 用样本轴线的图层 parentId 作为“分区根”（避免你说的“又建一套图层”）
      var axisLayer = doc.Layers[axisLayerIndex];
      if (axisLayer != null)
      {
        parentId = axisLayer.ParentLayerId;
        if (parentId == Guid.Empty) parentId = axisLayer.Id;
      }
    }

    // -------------------------
    // 3) 样本解析 -> 更新面板（注意：面板 A/B 需要从存储 offset 反推）
    // -------------------------
    string sampleName = sampleAxisObj.Attributes.Name ?? "Wallaxis";
    var sampleInfo = new AxisInfo();
    FillAxisInfoFromName(sampleName, sampleInfo);

    int sampleCapStOn = GetCapStart01(sampleName);
    int sampleCapEdOn = GetCapEnd01(sampleName);

    // 用你 EditWalls 同一套“反推面板 A/B”的逻辑
    double a = sampleInfo.OffsetL;
    double b = sampleInfo.OffsetR;

    double ai = sampleInfo.InsulLeftOffset;
    double bi = sampleInfo.InsulRightOffset;
    double aiForWall = sampleInfo.InsulLeftOn ? ai : 0.0;
    double biForWall = sampleInfo.InsulRightOn ? bi : 0.0;

    if (sampleInfo.UseInsulLeftAsOutline && !sampleInfo.UseInsulRightAsOutline)
    {
      a = sampleInfo.OffsetL + aiForWall;
      b = sampleInfo.OffsetR - aiForWall;
    }
    else if (sampleInfo.UseInsulRightAsOutline && !sampleInfo.UseInsulLeftAsOutline)
    {
      a = sampleInfo.OffsetL - biForWall;
      b = sampleInfo.OffsetR + biForWall;
    }

    var s2 = snapshot;
    s2.L = a;
    s2.R = b;
    s2.InsLOn = sampleInfo.InsulLeftOn;
    s2.InsROn = sampleInfo.InsulRightOn;
    s2.InsL = ai;
    s2.InsR = bi;
    s2.OutlineL = sampleInfo.UseInsulLeftAsOutline;
    s2.OutlineR = sampleInfo.UseInsulRightAsOutline;
    s2.WallKind = sampleInfo.WallType;
    panel.ApplySnapshot(s2);

    // -------------------------
    // 4) 动态预览（目标轴线越选越多，预览越多；面板改动实时生效）
    // -------------------------
    var preview = new RTZ3SingleToWallPreviewConduit(doc, panel);
    preview.AxisCurves.Clear();
    preview.Enabled = true;

    // 先把“预选目标”加进来：立刻隐藏+预览（满足你“点按钮就立刻工作”）
    for (int i = 0; i < targetAxisIds.Count; i++)
    {
      Guid tid = targetAxisIds[i];
      if (tid == Guid.Empty || tid == sampleAxisId) continue;

      HideRelatedObjectsForAxis(tid, parentId);
      if (i < targetAxisCurves.Count && targetAxisCurves[i] != null)
        preview.AxisCurves.Add(targetAxisCurves[i]);
    }
    doc.Views.Redraw();

    // -------------------------
    // 5) 选择目标轴线（只允许 post-select，避免预选死循环）
    // -------------------------
    while (true)
    {
      var goT = new Rhino.Input.Custom.GetObject();
      goT.SetCommandPrompt(
        targetAxisIds.Count == 0
          ? "格式刷：请选择【目标】墙体轴线（回车确认；Esc取消）"
          : "格式刷：继续选择目标墙体轴线（回车确认；Esc取消）");

      goT.GeometryFilter = ObjectType.Curve;
      goT.SubObjectSelect = false;

      // ✅ 关键：目标选择阶段禁用 preselect，彻底杜绝“预选轴线导致死循环卡死”
      goT.EnablePreSelect(false, true);
      goT.DeselectAllBeforePostSelect = false;
      goT.AcceptNothing(true);

      var gr = goT.Get();
      if (gr == GetResult.Nothing) break;
      if (gr == GetResult.Cancel) throw new OperationCanceledException();
      if (gr != GetResult.Object) throw new OperationCanceledException();

      for (int k = 0; k < goT.ObjectCount; k++)
      {
        var ro = goT.Object(k).Object() as CurveObject;
        if (ro == null) continue;
        if (!IsWallAxisObj(ro)) continue;
        if (ro.Attributes.LayerIndex != axisLayerIndex) continue;

        Guid tid = ro.Id;
        if (tid == Guid.Empty) continue;
        if (tid == sampleAxisId) continue;
        if (targetAxisIds.Contains(tid)) continue;

        var crv = ro.Geometry as Curve;
        if (crv == null) continue;

        targetAxisIds.Add(tid);
        HideRelatedObjectsForAxis(tid, parentId);

        preview.AxisCurves.Add(crv.DuplicateCurve());
        doc.Views.Redraw();
      }
    }

    if (targetAxisIds.Count == 0)
    {
      // 没选目标：当作取消，但把现场恢复干净
      preview.Enabled = false;
      RestoreHiddenObjects();
      RestoreLayerLocks();
      panel.ApplySnapshot(snapshot);
      panel.SetWallGenerateMode(WallGenerateMode.None);
      doc.Views.Redraw();
      return;
    }

    // -------------------------
    // 6) 写入 Name + 走你既有“轴线重建墙线”管线
    // -------------------------
    for (int i = 0; i < targetAxisIds.Count; i++)
    {
      var ro = doc.Objects.FindId(targetAxisIds[i]) as CurveObject;
      if (ro == null) continue;
      WriteSampleIntoAxisName(ro, sampleInfo, sampleCapStOn, sampleCapEdOn);
    }

    // 先退出预览，恢复显示（然后再删/重建）
    preview.Enabled = false;
    RestoreHiddenObjects();
    doc.Views.Redraw();

    // 找出需要一起重建的轴线（目标 + 与其相交的旧轴线）
    var axisToReplace = CollectIntersectingAxesForNewAxes(doc, axisLayerIndex, targetAxisIds);

    // 删除旧 cap / 旧墙线（锁层也能删）
    DeleteWallCapsByAxisIds(doc, axisToReplace, false);
    DeleteAllWallsAndInsulationForAxes(doc, axisToReplace);

    // 找图层索引（找不到就传 -1；Split 里会 Ensure）
    int wallLayerIndex     = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
    int wallGlasLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
    int wallStudLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
    int insulLayerIndex    = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");

    // Split + rebuild（内部会 Ensure 图层）
    SplitAxesAndRebuildWalls(
      doc,
      axisToReplace,
      targetAxisIds,
      wallLayerIndex,
      wallGlasLayerIndex,
      wallStudLayerIndex,
      axisLayerIndex,
      insulLayerIndex
      );

    // 按轴线 cap 标签补回封口
    // 按轴线 cap 标签补回封口 + 自动导角（与 Refresh/Redraw 保持一致）
    try
    {
        if (!RTZ3State.SuppressCapsOnce)
        {
         ApplyWallChamferForAxesToBeDeal(doc, parentId);
         ApplyWallCapsForAxesToBeDeal(doc);
         ApplyWallBigFallbackForOrphanEnds(doc, parentId);
         ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
        }
        else
        {
            RTZ3State.SuppressCapsOnce = false;
        }
    }
  catch (Exception ex)
 {
  RhinoApp.WriteLine("[FormatBrush][Cap/Chamfer] 异常：{0}", ex.Message);
 }

 RestoreLayerLocks();


    panel.SetWallGenerateMode(WallGenerateMode.None);
    doc.Views.Redraw();
  }
    catch (OperationCanceledException)
  {
   // Esc / 取消：恢复到“像无事发生”
   RestoreHiddenObjects();
   RestoreLayerLocks();
   panel.ApplySnapshot(snapshot);
   panel.SetWallGenerateMode(WallGenerateMode.None);
   doc.Views.Redraw();
  }

  catch (Exception ex)
  {
    RestoreHiddenObjects();
    RestoreLayerLocks();
    panel.ApplySnapshot(snapshot);
    panel.SetWallGenerateMode(WallGenerateMode.None);
    RhinoApp.WriteLine("格式刷异常: " + ex.Message);
    doc.Views.Redraw();
  }
  finally
  {
    // ★ 保险：命令结束后，强制锁回墙相关图层（不依赖 RestoreLayerLocks 的备份状态）
    try
    {
        // parentId 在 RunFormatBrushMode 里已经有（你前面就声明了 Guid parentId = Guid.Empty;）
        // 这里用 LockExistingWallLayers 强制锁回四个墙线图层
        int w = -1, wg = -1, ws = -1, ins = -1;
        LockExistingWallLayers(doc, parentId, ref w, ref wg, ref ws, ref ins);

        // 再强制锁回填充图层（你别处也是这么干的）
        ForceLockWallHatchLayer(doc, parentId);
    }
    catch { }
  }
}






// =====================模块01结束==============================




// ========================================================
// ===============  模块02 轴线处理模块  ===================
// ===============    各种函数   =================



// ★★ 统一的“轴线处理模块入口”：接收本次新写入的轴线 list，完成所有墙线/保温线生成与交点处理
void ProcessNewAxesForParent(
  RhinoDoc doc,
  Guid parentId,
  List<AxisInfo> axes,
  HashSet<Guid> ignoreAxisIds = null)
{

  
  if (doc == null) return;
  if (axes == null || axes.Count == 0) return;
  // ★ 总闸门：任何重叠直接中断
  double tol = doc.ModelAbsoluteTolerance;
  // ★ 这里把 ignoreAxisIds 透传进去
  if (AbortIfAnyAxisOverlap(doc, parentId, axes?.Select(a => a?.AxisCurve), tol, ignoreAxisIds))
    return;

  int wallLayerIndex     = -1;
  int wallGlasLayerIndex = -1;
  int wallStudLayerIndex = -1;
  int axisLayerIndex     = -1;
  int insulLayerIndex    = -1;

  // 1）先锁住当前分区下已有的墙线图层（输入模块不用管锁图层）
  LockExistingWallLayers(
    doc,
    parentId,
    ref wallLayerIndex,
    ref wallGlasLayerIndex,
    ref wallStudLayerIndex,
    ref insulLayerIndex);

  // 2）按需创建 / 复用本分区下的 PB-Wall / PB-Wall-Glas / PB-Wall-Stud / PB-Wall-Insul / PB-Wall-Axis
  EnsureWallLayersForAxes(
    doc,
    parentId,
    axes,
    ref wallLayerIndex,
    ref wallGlasLayerIndex,
    ref wallStudLayerIndex,
    ref axisLayerIndex,
    ref insulLayerIndex);

  // 3）真正把轴线 + 墙线 + 保温线写入文档，并做交点打断与重建
  CommitAxesAndWallsToDoc(
    doc,
    axes,
    wallLayerIndex,
    wallGlasLayerIndex,
    wallStudLayerIndex,
    axisLayerIndex,
    insulLayerIndex);

   // 4）墙线/保温线都已经生成完 + 节点处理完之后：做端头封口（从 axistobedeal 里取）
   try
   {
     if (!RTZ3State.SuppressCapsOnce)
     {
        ApplyWallChamferForAxesToBeDeal(doc, parentId);
        ApplyWallCapsForAxesToBeDeal(doc);
        ApplyWallBigFallbackForOrphanEnds(doc, parentId);
        ApplyWallHatchForConcreteAxesToBeDeal(doc, parentId);
       }
     else
     {
        RTZ3State.SuppressCapsOnce = false; // 用完就复位
     }
   }
   catch (Exception ex)
   {
     RhinoApp.WriteLine("[Cap] 封口异常：{0}", ex.Message);
   }

  doc.Views.Redraw();
}

// ===========================================================
// ==================  刷新按钮：主逻辑  =======================
// ===========================================================

// 刷新入口：在“当前激活图层的父级分区”内，找出被移动/编辑且 Name 未同步的轴线，
// 删除其旧墙线/保温线，更新轴线 Name，然后把这些轴线按“新轴线”送入你现有的交点处理管线。



// —— 小工具：把 Wallaxis Name 同步到真实数据（只更新 AxisId/Start/End/Axistype，其它参数尽量保留）——
string BuildSyncedWallaxisName(string oldName, Guid realId, Curve axisCrv)
{
  if (axisCrv == null) return oldName ?? "Wallaxis";

  string name = oldName ?? "";

  // 如果压根不是 Wallaxis，就给它补一套默认信息（你之前要求的默认值）
  if (string.IsNullOrEmpty(name) || !name.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
  {
    string startStr = FormatPointForName(axisCrv.PointAtStart);
    string endStr   = FormatPointForName(axisCrv.PointAtEnd);
    string axisType = GuessAxisTypeTag(axisCrv);

    // 默认：L=100 R=100 Walltype=block InsulL/R=80 全关
    return
      "Wallaxis"
      + "|AxisId=" + realId
      + "|Axistype=" + axisType
      + "|L=100|R=100"
      + "|Walltype=block"
      + "|InsulL=80|InsulR=80"
      + "|InsulLOn=0|InsulROn=0"
      + "|UseInsulLAsOutline=0|UseInsulRAsOutline=0"
      + "|Start=" + startStr
      + "|End=" + endStr;
  }

  // 只更新/追加：AxisId / Axistype / Start / End
  name = SetOrAppendTag(name, "AxisId", realId.ToString());
  name = SetOrAppendTag(name, "Axistype", GuessAxisTypeTag(axisCrv));
  name = SetOrAppendTag(name, "Start", FormatPointForName(axisCrv.PointAtStart));
  name = SetOrAppendTag(name, "End",   FormatPointForName(axisCrv.PointAtEnd));

  return name;
}

string GuessAxisTypeTag(Curve c)
{
  if (c == null) return "spline";
  // 你当前就三类：line / arc / spline（和你代码里 AxisCurveKind 一致）
  if (c is ArcCurve) return "arc";
  if (c.IsLinear())  return "line";
  return "spline";
}

string FormatPointForName(Point3d p)
{
  var ci = CultureInfo.InvariantCulture;
  return
    p.X.ToString(ci) + "," +
    p.Y.ToString(ci) + "," +
    p.Z.ToString(ci);
}

string SetOrAppendTag(string name, string key, string value)
{
  if (string.IsNullOrEmpty(name)) name = "Wallaxis";
  if (string.IsNullOrEmpty(key))  return name;

  var parts = new List<string>(name.Split('|'));
  bool replaced = false;

  for (int i = 0; i < parts.Count; i++)
  {
    int eq = parts[i].IndexOf('=');
    if (eq <= 0) continue;

    string k = parts[i].Substring(0, eq).Trim();
    if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
    {
      parts[i] = k + "=" + value;
      replaced = true;
      break;
    }
  }

  if (!replaced)
    parts.Add(key + "=" + value);

  return string.Join("|", parts);
}


// ★ 根据“本轮新轴线”在 PB-Wall-Axis 图层里几何求交，得到所有要重建的轴线 Id
List<Guid> CollectIntersectingAxesForNewAxes(
  RhinoDoc doc,
  int axisLayerIndex,
  List<Guid> newAxisIds)
{
  var resultSet = new HashSet<Guid>();

  if (doc == null) return new List<Guid>();
  if (newAxisIds == null || newAxisIds.Count == 0) return new List<Guid>();

    // ★ 本分区 parentId（用于兜底全图扫描时做父级图层树过滤）
    Guid parentId = Guid.Empty;

    // 优先：由 axisLayerIndex 反推 parent
    if (axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
    {
    var axisLayer = doc.Layers[axisLayerIndex];
    if (axisLayer != null)
    parentId = axisLayer.ParentLayerId;
    }

    // 兜底：如果 axisLayerIndex 不靠谱，就用第一根 newAxis 的图层反推 parent
    if (parentId == Guid.Empty && newAxisIds != null && newAxisIds.Count > 0)
    {
    var ro0 = doc.Objects.Find(newAxisIds[0]);
    if (ro0 != null)
     {
        int li0 = ro0.Attributes.LayerIndex;
        if (li0 >= 0 && li0 < doc.Layers.Count)
        {
        var lay0 = doc.Layers[li0];
        if (lay0 != null)
        parentId = lay0.ParentLayerId;
        }
     }
    }



  // 1）先拿到当前分区的轴线对象
  RhinoObject[] axisObjs = null;

  if (axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
  {
    var axisLayer = doc.Layers[axisLayerIndex];
    if (axisLayer != null)
      axisObjs = doc.Objects.FindByLayer(axisLayer);
  }

  // 兜底：如果没拿到，就扫描所有曲线
// 兜底：如果没拿到，就扫描所有曲线（但要限制在本 parentId 图层树内）
    if (axisObjs == null || axisObjs.Length == 0)
  {
    var all = doc.Objects.FindByObjectType(ObjectType.Curve);

    if (all != null && all.Length > 0 && parentId != Guid.Empty)
    {
        var filtered = new List<RhinoObject>(all.Length);
        foreach (var ro in all)
        {
         if (ro == null) continue;
        if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId)) continue;
         filtered.Add(ro);
        }
        axisObjs = filtered.ToArray();
    }
        else
    {
    // parentId 取不到就保持老行为，避免误判“一个都找不到”
    axisObjs = all;
    }
  }


  if (axisObjs == null || axisObjs.Length == 0)
    return new List<Guid>();

  double tol = doc.ModelAbsoluteTolerance;
  double tolInt = GetAxisIntersectionTol(doc);    // 求交用更宽

  foreach (var newId in newAxisIds)
  {
    var newObj = doc.Objects.Find(newId) as CurveObject;
    if (newObj == null) continue;

    var newCurve = newObj.Geometry as Curve;
    if (newCurve == null) continue;

    var newBox = newCurve.GetBoundingBox(true);
    if (!newBox.IsValid) continue;
    newBox.Inflate(tolInt); // ★关键：膨胀一点

    // 新轴线自己一定在集合里
    resultSet.Add(newId);

    foreach (var obj in axisObjs)
    {
      var co = obj as CurveObject;
      if (co == null) continue;

      var crv = co.Geometry as Curve;
      if (crv == null) continue;

     // ★★ 关键过滤：排除“复制/移动过但 Name 未同步”的脏轴线
     // 规则：
     // 1）如果 Name 里有 AxisId 且 AxisId ≠ 对象 Id → 排除
     // 2）如果 Name 里有 Start/End，且任意一端与当前几何起终点不一致（距离 > tol）→ 排除
     string axisName = co.Attributes.Name;

     
      // ★ 新增：Name 为空的一律当空气（不参与求交候选）
      if (string.IsNullOrWhiteSpace(axisName))
      continue;

      // （可选但强烈建议）非 Wallaxis 也当空气
      if (!axisName.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
      continue;

     if (!string.IsNullOrEmpty(axisName))
     {
        bool shouldSkip = false;

        // ① Id 比对
        Guid axisIdFromName;
        if (TryParseAxisIdFromName(axisName, out axisIdFromName))
        {
          if (axisIdFromName != co.Id)
            shouldSkip = true;
        }

        // ② 起点/终点比对（只要有一端写在 Name 里，就做检查）
        Point3d nameStart, nameEnd;
        Point3d geoStart = crv.PointAtStart;
        Point3d geoEnd   = crv.PointAtEnd;

        if (!shouldSkip && TryParsePointFromName(axisName, "Start", out nameStart))
        {
          if (geoStart.DistanceTo(nameStart) > tol)
            shouldSkip = true;
        }

        if (!shouldSkip && TryParsePointFromName(axisName, "End", out nameEnd))
       {
          if (geoEnd.DistanceTo(nameEnd) > tol)
        shouldSkip = true;
       }

        if (shouldSkip)
          continue; // ← 直接排除出“几何求交候选集合”
     }

      // 自己已经加过了
      if (co.Id == newId)
        continue;

      // 2）BoundingBox 粗筛
      var box = crv.GetBoundingBox(true);
      if (!box.IsValid) continue;
      box.Inflate(tolInt);    // ★关键：膨胀一点
      if (!BoundingBoxesIntersect(newBox, box))
        continue;

      // 3）CurveCurve 精确判断是否真有交点或重叠
      var ccx = Rhino.Geometry.Intersect.Intersection.CurveCurve(
                  newCurve, crv, tol, tol);
      if (ccx == null || ccx.Count == 0)
        continue;

      bool hasIntersection = false;
      foreach (var x in ccx)
      {
        if (x.IsPoint || x.IsOverlap)
        {
          hasIntersection = true;
          break;
        }
      }

      if (!hasIntersection)
     {
        if (CurvesCloseAtEndpoints(newCurve, crv, tolInt))
        hasIntersection = true;
     }

      if (!hasIntersection)
        continue;

      resultSet.Add(co.Id);
    }
  }

  return new List<Guid>(resultSet);
}

// ★ 把轴线 Name 里的参数解析回 AxisInfo（除了 AxisId / Start / End）
bool FillAxisInfoFromName(string name, AxisInfo info)
{
  if (info == null) return false;
  if (string.IsNullOrEmpty(name)) return false;
  if (!name.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
    return false;

  string[] parts = name.Split('|');
  var c = CultureInfo.InvariantCulture;

  for (int i = 1; i < parts.Length; i++)
  {
    string part = parts[i];
    if (string.IsNullOrEmpty(part)) continue;

    int eq = part.IndexOf('=');
    if (eq <= 0 || eq >= part.Length - 1) continue;

    string key   = part.Substring(0, eq);
    string value = part.Substring(eq + 1);

    switch (key)
    {
      case "Axistype":
        info.AxisType = ParseAxisTypeTagOrDefault(value);
        break;

      case "L":
        double dL;
        if (double.TryParse(value, NumberStyles.Float, c, out dL))
          info.OffsetL = dL;
        break;

      case "R":
        double dR;
        if (double.TryParse(value, NumberStyles.Float, c, out dR))
          info.OffsetR = dR;
        break;

      case "Walltype":
        info.WallType = ParseWallKindTagOrDefault(value);
        break;

      case "InsulL":
        double iL;
        if (double.TryParse(value, NumberStyles.Float, c, out iL))
          info.InsulLeftOffset = iL;
        break;

      case "InsulR":
        double iR;
        if (double.TryParse(value, NumberStyles.Float, c, out iR))
          info.InsulRightOffset = iR;
        break;

      case "InsulLOn":
        info.InsulLeftOn = (value == "1");
        break;

      case "InsulROn":
        info.InsulRightOn = (value == "1");
        break;

      case "UseInsulLAsOutline":
        info.UseInsulLeftAsOutline = (value == "1");
        break;

      case "UseInsulRAsOutline":
        info.UseInsulRightAsOutline = (value == "1");
        break;

      // Start / End / AxisId 在几何上重新计算，这里忽略
    }
  }

  return true;
}

// ★ 反向：把 Name 里的 wall tag 还原成 WallKind
WallKind ParseWallKindTagOrDefault(string tag)
{
  if (string.IsNullOrEmpty(tag)) return WallKind.Concrete;

  switch (tag.ToLowerInvariant())
  {
    case "concrete": return WallKind.Concrete;
    case "block":    return WallKind.Block;
    case "glass":    return WallKind.Glass;
    case "stud":     return WallKind.Stud;
  }

  return WallKind.Concrete;
}

// ★ 反向：把 "line/arc/spline" 还原成 AxisCurveKind
AxisCurveKind ParseAxisTypeTagOrDefault(string tag)
{
  if (string.IsNullOrEmpty(tag)) return AxisCurveKind.Line;

  switch (tag.ToLowerInvariant())
  {
    case "line":   return AxisCurveKind.Line;
    case "arc":    return AxisCurveKind.Arc;
    case "spline": return AxisCurveKind.Spline;
  }

  return AxisCurveKind.Line;
}

// 命令启动时：如果当前分区下已经有 PB-Wall / PB-Wall-Glas / PB-Wall-Stud / PB-Wall-Insul，就把它们重新锁上
void LockExistingWallLayers(
  RhinoDoc doc,
  Guid parentId,
  ref int wallLayerIndex,
  ref int wallGlasLayerIndex,
  ref int wallStudLayerIndex,
  ref int insulLayerIndex)
{
  if (doc == null) return;

  // 小工具：按名字找同级图层，并锁定它本身
  void LockSingleLayer(ref int layerIndex, string layerName)
  {
    // 如果这次命令里还没拿到索引，就再找一次
    if (layerIndex < 0)
      layerIndex = FindSiblingLayerIndex(doc, parentId, layerName);

    if (layerIndex < 0)
      return;

    var layer = doc.Layers[layerIndex];
    if (layer == null)
      return;

    if (!layer.IsLocked)
    {
      layer.IsLocked = true;                 // ★ 只锁这一层本身
      doc.Layers.Modify(layer, layerIndex, true);
    }
  }

  // 依次锁住 4 个墙相关图层
  LockSingleLayer(ref wallLayerIndex,     "PB-Wall");
  LockSingleLayer(ref wallGlasLayerIndex, "PB-Wall-Glas");
  LockSingleLayer(ref wallStudLayerIndex, "PB-Wall-Stud");
  LockSingleLayer(ref insulLayerIndex,    "PB-Wall-Insul");
}

// 针对一轮 currentAxes：按需创建 / 复用各类图层
void EnsureWallLayersForAxes(
  RhinoDoc doc,
  Guid parentId,
  List<AxisInfo> axes,
  ref int wallLayerIndex,
  ref int wallGlasLayerIndex,
  ref int wallStudLayerIndex,
  ref int axisLayerIndex,
  ref int insulLayerIndex)
{
  if (doc == null) return;
  if (axes == null || axes.Count == 0) return;

  bool needWall  = false; // PB-Wall（Concrete / Block）
  bool needGlas  = false; // PB-Wall-Glas（Glass）
  bool needStud  = false; // PB-Wall-Stud（Stud）
  bool needInsul = false; // PB-Wall-Insul（保温外皮）

  foreach (var info in axes)
  {
    if (info == null) continue;
    if (info.WallType == WallKind.Concrete || info.WallType == WallKind.Block)
      needWall = true;
    else if (info.WallType == WallKind.Glass)
      needGlas = true;
    else if (info.WallType == WallKind.Stud)
      needStud = true;

    if ((info.InsulLeftOn  && info.InsulLeftOffset  > 0.0) ||
        (info.InsulRightOn && info.InsulRightOffset > 0.0))
      needInsul = true;
  }

  // 4.1 PB-Wall（钢筋砼墙 / 填充墙）
  if (needWall && wallLayerIndex < 0)
  {
    wallLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall");
    if (wallLayerIndex < 0)
    {
      Layer wallLayer = new Layer();
      wallLayer.Name          = "PB-Wall";
      wallLayer.ParentLayerId = parentId;
      wallLayer.Color         = SDColor.White;
      wallLayer.PlotColor     = SDColor.Black;
      wallLayer.PlotWeight    = 0.3;

      wallLayer.IsLocked      = true;   // ★ 新增：默认锁定
      wallLayerIndex = doc.Layers.Add(wallLayer);
    }
  }

  // 4.2 PB-Wall-Glas（玻璃幕墙）
  if (needGlas && wallGlasLayerIndex < 0)
  {
    wallGlasLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
    if (wallGlasLayerIndex < 0)
    {
      Layer glasLayer = new Layer();
      glasLayer.Name          = "PB-Wall-Glas";
      glasLayer.ParentLayerId = parentId;
      glasLayer.Color         = SDColor.FromArgb(120, 209, 159);
      glasLayer.PlotColor     = SDColor.FromArgb(35, 35, 35);
      glasLayer.PlotWeight    = 0.1;

      glasLayer.IsLocked      = true;   // ★ 新增：默认锁定
      wallGlasLayerIndex = doc.Layers.Add(glasLayer);
    }
  }

  // 4.3 PB-Wall-Stud（轻质隔墙）
  if (needStud && wallStudLayerIndex < 0)
  {
    wallStudLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");
    if (wallStudLayerIndex < 0)
    {
      Layer studLayer = new Layer();
      studLayer.Name          = "PB-Wall-Stud";
      studLayer.ParentLayerId = parentId;
      studLayer.Color         = SDColor.FromArgb(64, 146, 146);
      studLayer.PlotColor     = SDColor.Black;
      studLayer.PlotWeight    = 0.15;
      studLayer.IsLocked      = true;   // ★ 新增：默认锁定
      wallStudLayerIndex = doc.Layers.Add(studLayer);
    }
  }

  // 4.3.5 PB-Wall-Insul（保温外轮廓线）
  if (needInsul && insulLayerIndex < 0)
  {
    insulLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Insul");
    if (insulLayerIndex < 0)
    {
      Layer insLayer = new Layer();
      insLayer.Name          = "PB-Wall-Insul";
      insLayer.ParentLayerId = parentId;
      insLayer.Color         = SDColor.FromArgb(173, 94, 94);  // ★ 需要改颜色就在这里改
      insLayer.PlotColor     = SDColor.FromArgb(0, 0, 0);
      insLayer.PlotWeight    = 0.1;
      insLayer.IsLocked      = true;   // ★ 新增：默认锁定
      insulLayerIndex = doc.Layers.Add(insLayer);
    }
  }

  // 4.4 PB-Wall-Axis（轴线层，统一不打印）
  if (axisLayerIndex < 0)
  {
    axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
    if (axisLayerIndex < 0)
    {
      Layer axisLayer = new Layer();
      axisLayer.Name          = "PB-Wall-Axis";
      axisLayer.ParentLayerId = parentId;
      axisLayer.Color         = SDColor.FromArgb(96, 56, 0);
      axisLayer.PlotWeight    = -1.0; // ⭐ 不打印
      axisLayerIndex = doc.Layers.Add(axisLayer);
    }
  }

  // 再保险：确保轴线层 PlotWeight 始终为 -1（不打印）
  if (axisLayerIndex >= 0)
  {
    Layer axisLayer = doc.Layers[axisLayerIndex];
    if (axisLayer != null && axisLayer.PlotWeight >= 0.0)
    {
      axisLayer.PlotWeight = -1.0;
      doc.Layers.Modify(axisLayer, axisLayerIndex, true);
    }
  }
}

// 把一轮 currentAxes 真正写入文档（轴线 + 左右墙线 + 保温线 + Name 标签 + 交点再处理）
void CommitAxesAndWallsToDoc(
  RhinoDoc doc,
  List<AxisInfo> axes,
  int wallLayerIndex,
  int wallGlasLayerIndex,
  int wallStudLayerIndex,
  int axisLayerIndex,
  int insulLayerIndex)
{
  if (doc == null) return;
  if (axes == null || axes.Count == 0) return;
  if (axisLayerIndex < 0) return;


  double tol = doc.ModelAbsoluteTolerance;

  // ★ 新增：若轴线图层当前被锁定，则临时解锁（本轮全部操作结束后再锁回去）
  bool axisLayerWasLocked = false;
  if (axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
  {
    var axisLayer = doc.Layers[axisLayerIndex];
    if (axisLayer != null && axisLayer.IsLocked)
    {
      axisLayerWasLocked = true;
      axisLayer.IsLocked = false;
      doc.Layers.Modify(axisLayer, axisLayerIndex, true);
    }
  }

  // ★ 记录本轮所有新生成的轴线 Id
  var newAxisIdsThisRound = new List<Guid>();

  foreach (var info in axes)
  {
    if (info == null || info.AxisCurve == null)
      continue;

    string wallTag     = GetWallKindTag(info.WallType);
    string axisTypeTag = GetAxisTypeTag(info.AxisType);
    string startStr    = FormatPoint(info.Start);
    string endStr      = FormatPoint(info.End);


    // 1）先添加轴线，拿到 AxisId
    var axisAttr = new ObjectAttributes();
    axisAttr.LayerIndex = axisLayerIndex;
    axisAttr.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer;

    Guid axisId = doc.Objects.AddCurve(info.AxisCurve, axisAttr);
    newAxisIdsThisRound.Add(axisId);

    // 更新轴线 Name：含 AxisId / L / R / Walltype / 起终点坐标 / 保温参数
    var axisObj = doc.Objects.Find(axisId);
    if (axisObj != null)
    {
      var aAttr = axisObj.Attributes;
      aAttr.Name =
        $"Wallaxis|AxisId={axisId}" +
        $"|Axistype={axisTypeTag}" +
        $"|L={info.OffsetL}" +
        $"|R={info.OffsetR}" +
        $"|Walltype={wallTag}" +
        $"|InsulL={info.InsulLeftOffset}" +
        $"|InsulR={info.InsulRightOffset}" +
        $"|InsulLOn={(info.InsulLeftOn ? 1 : 0)}" +
        $"|InsulROn={(info.InsulRightOn ? 1 : 0)}" +
        $"|UseInsulLAsOutline={(info.UseInsulLeftAsOutline ? 1 : 0)}" +
        $"|UseInsulRAsOutline={(info.UseInsulRightAsOutline ? 1 : 0)}" +
        $"|Start={startStr}" +
        $"|End={endStr}";
      doc.Objects.ModifyAttributes(axisObj, aAttr, true);
    }

    // 2）墙线（根据墙类型分配到不同图层）
    int targetLayerIndex = -1;
    if (info.WallType == WallKind.Concrete || info.WallType == WallKind.Block)
      targetLayerIndex = wallLayerIndex;
    else if (info.WallType == WallKind.Glass)
      targetLayerIndex = wallGlasLayerIndex;
    else if (info.WallType == WallKind.Stud)
      targetLayerIndex = wallStudLayerIndex;

    if (targetLayerIndex >= 0)
    {
      // 左线
      if (info.WallLeft != null)
      {
        Point3d wlStart   = info.WallLeft.PointAtStart;
        Point3d wlEnd     = info.WallLeft.PointAtEnd;
        string  wlStartStr = FormatPoint(wlStart);
        string  wlEndStr   = FormatPoint(wlEnd);

        var wallAttrL = new ObjectAttributes();
        wallAttrL.LayerIndex = targetLayerIndex;
        wallAttrL.Name =
          $"Wall|AxisId={axisId}" +
          $"|Side=Left" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wlStartStr}" +
          $"|End={wlEndStr}";

        doc.Objects.AddCurve(info.WallLeft, wallAttrL);
      }

      // 右线
      if (info.WallRight != null)
      {
        Point3d wrStart   = info.WallRight.PointAtStart;
        Point3d wrEnd     = info.WallRight.PointAtEnd;
        string  wrStartStr = FormatPoint(wrStart);
        string  wrEndStr   = FormatPoint(wrEnd);

        var wallAttrR = new ObjectAttributes();
        wallAttrR.LayerIndex = targetLayerIndex;
        wallAttrR.Name =
          $"Wall|AxisId={axisId}" +
          $"|Side=Right" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wrStartStr}" +
          $"|End={wrEndStr}";

        doc.Objects.AddCurve(info.WallRight, wallAttrR);
      }
    }
    // ★ 1.1.2：玻璃幕墙内墙线（Wallinside）
    // 规则：
    // - 内墙线与外墙线同层（PB-Wall-Glas）
    // - 名称前缀：Wallinside|AxisId=...|Side=Left/Right|Walltype=...|...
    if (info.WallType == WallKind.Glass)
    {
      // 防御性：如果前面没算出来（例如旧数据流程），这里补算一次
      if (info.WallinsideLeft == null || info.WallinsideRight == null)
      {
        Plane pl;
        if (BuildAxisPlane(info, out pl))
        {
          Curve wiL, wiR;
          BuildGlassInsideWallPair(info, pl, tol, out wiL, out wiR);
          info.WallinsideLeft  = wiL;
          info.WallinsideRight = wiR;
        }
      }

      if (info.WallinsideLeft != null)
      {
        Point3d wilStart = info.WallinsideLeft.PointAtStart;
        Point3d wilEnd   = info.WallinsideLeft.PointAtEnd;

        string wilStartStr = FormatPoint(wilStart);
        string wilEndStr   = FormatPoint(wilEnd);

        var wallAttrIL = new ObjectAttributes();
        wallAttrIL.LayerIndex = targetLayerIndex;
        wallAttrIL.ColorSource = ObjectColorSource.ColorFromLayer;
        wallAttrIL.Name =
          $"Wallinside|AxisId={axisId}" +
          $"|Side=Left" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wilStartStr}" +
          $"|End={wilEndStr}";

        doc.Objects.AddCurve(info.WallinsideLeft, wallAttrIL);
      }

      if (info.WallinsideRight != null)
      {
        Point3d wirStart = info.WallinsideRight.PointAtStart;
        Point3d wirEnd   = info.WallinsideRight.PointAtEnd;

        string wirStartStr = FormatPoint(wirStart);
        string wirEndStr   = FormatPoint(wirEnd);

        var wallAttrIR = new ObjectAttributes();
        wallAttrIR.LayerIndex = targetLayerIndex;
        wallAttrIR.ColorSource = ObjectColorSource.ColorFromLayer;
        wallAttrIR.Name =
          $"Wallinside|AxisId={axisId}" +
          $"|Side=Right" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wirStartStr}" +
          $"|End={wirEndStr}";

        doc.Objects.AddCurve(info.WallinsideRight, wallAttrIR);
      }
    }



    // 3）保温外皮线：统一放到 PB-Wall-Insul 图层（如果存在）
    if (insulLayerIndex >= 0)
    {
      if (info.InsulLeftOn && info.InsulLeftCurve != null)
      {
        Point3d ilStart   = info.InsulLeftCurve.PointAtStart;
        Point3d ilEnd     = info.InsulLeftCurve.PointAtEnd;
        string  ilStartStr = FormatPoint(ilStart);
        string  ilEndStr   = FormatPoint(ilEnd);

        var insAttrL = new ObjectAttributes();
        insAttrL.LayerIndex = insulLayerIndex;
        insAttrL.Name =
          $"Insulation|AxisId={axisId}" +
          $"|Side=Left" +
          $"|Offset={info.InsulLeftOffset}" +
          $"|Start={ilStartStr}" +
          $"|End={ilEndStr}";

        doc.Objects.AddCurve(info.InsulLeftCurve, insAttrL);
      }

      if (info.InsulRightOn && info.InsulRightCurve != null)
      {
        Point3d irStart   = info.InsulRightCurve.PointAtStart;
        Point3d irEnd     = info.InsulRightCurve.PointAtEnd;
        string  irStartStr = FormatPoint(irStart);
        string  irEndStr   = FormatPoint(irEnd);

        var insAttrR = new ObjectAttributes();
        insAttrR.LayerIndex = insulLayerIndex;
        insAttrR.Name =
          $"Insulation|AxisId={axisId}" +
          $"|Side=Right" +
          $"|Offset={info.InsulRightOffset}" +
          $"|Start={irStartStr}" +
          $"|End={irEndStr}";

        doc.Objects.AddCurve(info.InsulRightCurve, insAttrR);
      }
    }
  }


  // ★★ 第 4 + 5 步：几何求交 → 删墙线 / 保温线 → 打断轴线 → 重建墙线 / 保温线
  var axisToReplace = CollectIntersectingAxesForNewAxes(doc, axisLayerIndex, newAxisIdsThisRound);

  if (axisToReplace != null && axisToReplace.Count > 0)
  { 
    // ★【必须放在这里】：在 SplitAxesAndRebuildWalls 删旧轴线之前，先删旧 cap
    int removedCaps = DeleteWallCapsByAxisIds(doc, axisToReplace, deleteOrphanCaps: false);
    RhinoApp.WriteLine("[Cap] 预清理旧cap：删除 {0} 条", removedCaps);
    // 4）把这些轴线对应的所有墙线 / 保温线删掉（包括本轮新轴线的）
    DeleteAllWallsAndInsulationForAxes(doc, axisToReplace);

    // 5）把新轴线与跟它相交的旧轴线一起互相打断，并为打断后的所有轴线片段重建墙线 / 保温线
    SplitAxesAndRebuildWalls(
      doc,
      axisToReplace,
      newAxisIdsThisRound,
      wallLayerIndex,
      wallGlasLayerIndex,
      wallStudLayerIndex,
      axisLayerIndex,
      insulLayerIndex);
  }

  // ==================== ★ 轴线层锁恢复：放在函数最后 ★ ====================
  if (axisLayerWasLocked && axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
 {
  var al = doc.Layers[axisLayerIndex];
  if (al != null)
  {
    al.IsLocked = true;
    doc.Layers.Modify(al, axisLayerIndex, true);
  }
 }


}

// ★ 根据 axisToReplace，把这些轴线对应的所有墙线 / 保温线删掉（支持临时解锁图层）
void DeleteAllWallsAndInsulationForAxes(
  RhinoDoc doc,
  List<Guid> axisToReplace)
{
  if (doc == null) return;
  if (axisToReplace == null || axisToReplace.Count == 0) return;

  var axisSet = new HashSet<Guid>(axisToReplace);
  var layerLockBackup = new Dictionary<int, bool>();

  // ★ 当前工作父级（父级为空则用当前层自身当父级根）
    Guid parentId = Guid.Empty;
    int curLi = doc.Layers.CurrentLayerIndex;
    if (curLi >= 0 && curLi < doc.Layers.Count)
 {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
    parentId = curLay.ParentLayerId;
    if (parentId == Guid.Empty) parentId = curLay.Id;
    }
 }

  // 小工具：在删除前临时解锁所在图层
  void EnsureLayerUnlocked(int layerIndex)
  {
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
      return;

    if (layerLockBackup.ContainsKey(layerIndex))
      return;

    var layer = doc.Layers[layerIndex];
    if (layer == null)
      return;

    // 记下原来的锁定状态，然后如果是锁定就先解锁
    layerLockBackup[layerIndex] = layer.IsLocked;
    if (layer.IsLocked)
    {
      layer.IsLocked = false;
      doc.Layers.Modify(layer, layerIndex, true);
    }
  }

  // 所有曲线对象里找墙线 / 保温线
  var allCurves = doc.Objects.FindByObjectType(ObjectType.Curve);
  if (allCurves == null || allCurves.Length == 0)
    return;

  foreach (var obj in allCurves)
  {
    if (obj == null) continue;

    int li = obj.Attributes.LayerIndex;
    if (li < 0 || li >= doc.Layers.Count) continue;
    if (parentId != Guid.Empty && !IsLayerUnderParent(doc, li, parentId)) continue;


    var name = obj.Attributes.Name;
    if (string.IsNullOrEmpty(name))
      continue;

    // 只处理 "Wall|..." / "Wallinside|..." / "Insulation|..." 的对象
    if (!name.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("Wallinside|", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("Insulation|", StringComparison.OrdinalIgnoreCase))
      continue;

    Guid axisId;
    if (!TryParseAxisIdFromName(name, out axisId))
      continue;

    if (!axisSet.Contains(axisId))
      continue;

    // 找到了属于这些轴线的墙线 / 保温线 → 解锁图层后删除
    int layerIndex = obj.Attributes.LayerIndex;
    EnsureLayerUnlocked(layerIndex);

    // ★ 1.3.3：删前缓存 Name 中的额外 token（如 GeoId=...），供后续重建继承
    try { NameCarryState.StoreCurveExtras(axisId, name); } catch { }

    doc.Objects.Delete(obj, true);
  }


  // ★ 同步删除：墙填充 Hatch（WallHatch|AxisId=...）
  var allHatches = doc.Objects.FindByObjectType(ObjectType.Hatch);
  if (allHatches != null)
  {
    foreach (var hobj in allHatches)
    {
      if (hobj == null) continue;

      int hli = hobj.Attributes.LayerIndex;
      if (!IsLayerUnderParent(doc, hli, parentId))
        continue;

      string name = hobj.Attributes.Name ?? "";
      if (!name.StartsWith("WallHatch|", StringComparison.OrdinalIgnoreCase))
        continue;

      Guid axisId;
      if (!TryParseAxisIdFromName(name, out axisId))
        continue;

      if (!axisSet.Contains(axisId))
        continue;

      EnsureLayerUnlocked(hli);
      doc.Objects.Delete(hobj, true);
    }
  }


  // 删除完成后，把图层锁定状态恢复
  foreach (var kv in layerLockBackup)
  {
    int layerIndex = kv.Key;
    bool wasLocked = kv.Value;

    if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
      continue;

    var layer = doc.Layers[layerIndex];
    if (layer == null)
      continue;

    layer.IsLocked = wasLocked;
    doc.Layers.Modify(layer, layerIndex, true);
  }


  // ★ 强制锁回填充图层（防止用户误解锁）
  ForceLockWallHatchLayer(doc, parentId);
}

void SplitAxesAndRebuildWalls(
  RhinoDoc doc,
  List<Guid> axisToReplace,
  List<Guid> newAxisIdsThisRound,
  int wallLayerIndex,
  int wallGlasLayerIndex,
  int wallStudLayerIndex,
  int axisLayerIndex,
  int insulLayerIndex)
{
  if (doc == null) return;
  if (axisLayerIndex < 0) return;
  if (axisToReplace == null || axisToReplace.Count == 0) return;
  
    // ★ 每次重建前先清空“待处理轴线”集合
  RTZ3State.axistobedeal.Clear();
  var finalAxisIds = new List<Guid>(); //这个是给axistobedeal轴线标红增加的

  // 1）从文档中把这批轴线找出来，解析 Name → AxisInfo
  var axisInfos = new Dictionary<Guid, AxisInfo>();
  var axisObjs  = new Dictionary<Guid, CurveObject>();

  foreach (var id in axisToReplace)
  {
    var obj = doc.Objects.Find(id) as CurveObject;
    if (obj == null) continue;

    // 如果 name 为空或不是 Wallaxis，跳过（你原有规则）
    string n = obj.Attributes.Name ?? "";
    if (string.IsNullOrWhiteSpace(n)) continue;
    if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase)) continue;

    // ★ 1.3.3：缓存“非本脚本维护”的额外 token（例如 GeoId=...）
    try { NameCarryState.StoreAxisExtras(id, n); } catch { }

    var curve = obj.Geometry as Curve;
    if (curve == null) continue;

    var info = new AxisInfo();
    info.AxisCurve = curve.DuplicateCurve();
    info.Start     = info.AxisCurve.PointAtStart;
    info.End       = info.AxisCurve.PointAtEnd;

    FillAxisInfoFromName(obj.Attributes.Name, info);

    axisInfos[id] = info;
    axisObjs[id]  = obj;
  }

  if (axisInfos.Count == 0)
    return;

      // ★ 新增：确保本分区下缺失的墙图层存在
  // 解决：当没有 PB-Wall-Glas / PB-Wall-Stud 图层时，Glass/Stud 墙线落图 targetLayerIndex=-1 导致“不生成”
  Guid parentId = Guid.Empty;
  if (axisLayerIndex >= 0 && axisLayerIndex < doc.Layers.Count)
  {
    var axisLayer = doc.Layers[axisLayerIndex];
    if (axisLayer != null)
    {
      parentId = axisLayer.ParentLayerId;
      if (parentId == Guid.Empty) parentId = axisLayer.Id; // 顶层分区
    }
  }

  if (parentId != Guid.Empty)
  {
    var ensureAxes = new List<AxisInfo>();
    foreach (var kv in axisInfos) ensureAxes.Add(kv.Value);

    EnsureWallLayersForAxes(
      doc,
      parentId,
      ensureAxes,
      ref wallLayerIndex,
      ref wallGlasLayerIndex,
      ref wallStudLayerIndex,
      ref axisLayerIndex,
      ref insulLayerIndex);
  }


  // 2）过滤一下“新轴线列表”——只保留确实在 axisInfos 里的那部分
  var newIds = new List<Guid>();
  if (newAxisIdsThisRound != null)
  {
    foreach (var id in newAxisIdsThisRound)
    {
      if (axisInfos.ContainsKey(id))
        newIds.Add(id);
    }
  }

  // 理论上不会走到这一步，但为了保险：如果一个新轴线都没有，就把全部轴线当成“新”来打断
  if (newIds.Count == 0)
    newIds.AddRange(axisInfos.Keys);

  // 3）计算“交点参数”：只考虑“至少有一根是新轴线”的轴线对
  var splitParams = new Dictionary<Guid, List<double>>();
  double tol = doc.ModelAbsoluteTolerance;
  double tolInt = GetAxisIntersectionTol(doc);  // 求交用更宽
  

  foreach (var newId in newIds)
  {
    AxisInfo newInfo;
    if (!axisInfos.TryGetValue(newId, out newInfo))
      continue;

    Curve newCurve = newInfo.AxisCurve;
    if (newCurve == null)
      continue;

    foreach (var kv in axisInfos)
    {
      Guid otherId = kv.Key;
      if (otherId == newId) continue;   // 跳过自己

      Curve otherCurve = kv.Value.AxisCurve;
      if (otherCurve == null)
        continue;

      var ccx = Rhino.Geometry.Intersect.Intersection.CurveCurve(
                  newCurve, otherCurve, tol, tol); 
      if (ccx == null || ccx.Count == 0)
        continue;

      foreach (var x in ccx)
      {
        if (!x.IsPoint && !x.IsOverlap)
          continue;

        AddSplitParam(splitParams, newId,   x.ParameterA, newCurve.Domain,   tol);
        AddSplitParam(splitParams, otherId, x.ParameterB, otherCurve.Domain, tol);
      }
    }
  }

  // 4）对每一根轴线：先删原轴线，再按“打断后的片段列表”重建 Axis + 墙线 + 保温线
  foreach (var kv in axisInfos)
  {
    Guid     axisId  = kv.Key;
    AxisInfo srcInfo = kv.Value;
    Curve    srcCurve = srcInfo.AxisCurve;
    if (srcCurve == null) continue;

    // 4.1 整理这根轴线的打断参数
    List<double> tList;
    if (!splitParams.TryGetValue(axisId, out tList) || tList == null || tList.Count == 0)
    {
      // 没有交点：整根作为一个片段
      tList = null;
    }
    else
    {
      tList.Sort();
      var domain  = srcCurve.Domain;
      var cleaned = new List<double>();
      double last = double.NaN;

      foreach (var t in tList)
      {
        // 端点附近的交点不需要再打断
        if (t <= domain.T0 + tol || t >= domain.T1 - tol)
          continue;

        if (cleaned.Count > 0 && Math.Abs(t - last) < tol * 5.0)
          continue;

        cleaned.Add(t);
        last = t;
      }

      if (cleaned.Count == 0)
        tList = null;
      else
        tList = cleaned;
    }

    // 4.2 删掉原来的轴线（包括本轮新轴线和与之相交的旧轴线）
    CurveObject oldObj;
    if (axisObjs.TryGetValue(axisId, out oldObj) && oldObj != null)
      doc.Objects.Delete(oldObj, true);

    // 4.3 得到被打断后的所有片段
    Curve[] pieces;
    if (tList == null)
    {
      pieces = new Curve[] { srcCurve };
    }
    else
    {
      pieces = srcCurve.Split(tList);
    }

    if (pieces == null || pieces.Length == 0)
      continue;

    // 4.4 为每个片段重建 Axis + 墙线 + 保温线
    foreach (var piece in pieces)
    {
      if (piece == null || !piece.IsValid)
        continue;

      var segInfo = new AxisInfo();
      segInfo.AxisCurve = piece;
      segInfo.Start     = piece.PointAtStart;
      segInfo.End       = piece.PointAtEnd;


      // ★ 1.3.3：记录来源旧 AxisId，用于 Name 额外 token 继承
      segInfo.SourceAxisId = axisId;
      // 继承原轴线的“厚度 / 类型 / 保温”信息
      segInfo.OffsetL               = srcInfo.OffsetL;
      segInfo.OffsetR               = srcInfo.OffsetR;
      segInfo.WallType              = srcInfo.WallType;
      segInfo.AxisType              = srcInfo.AxisType;
      segInfo.InsulLeftOffset       = srcInfo.InsulLeftOffset;
      segInfo.InsulRightOffset      = srcInfo.InsulRightOffset;
      segInfo.InsulLeftOn           = srcInfo.InsulLeftOn;
      segInfo.InsulRightOn          = srcInfo.InsulRightOn;
      segInfo.UseInsulLeftAsOutline  = srcInfo.UseInsulLeftAsOutline;
      segInfo.UseInsulRightAsOutline = srcInfo.UseInsulRightAsOutline;

      Plane pl;
      if (BuildAxisPlane(segInfo, out pl))
      {
        Curve wallL, wallR;
        BuildOffsetWallPair(segInfo, pl, tol, out wallL, out wallR);
        segInfo.WallLeft  = wallL;
        segInfo.WallRight = wallR;

        Curve insL, insR;
        BuildInsulationPair(segInfo, pl, tol, out insL, out insR);
        segInfo.InsulLeftCurve  = insL;
        segInfo.InsulRightCurve = insR;
      }

      // 真正写回文档：新的 AxisId + 对应墙线 / 保温线
      Guid newAxisId = CommitSingleAxisToDoc(
        doc,
        segInfo,
        wallLayerIndex,
        wallGlasLayerIndex,
        wallStudLayerIndex,
        axisLayerIndex,
        insulLayerIndex);
      
      // ★ 统一收集到 axistobedeal 里
        if (newAxisId != Guid.Empty)
          finalAxisIds.Add(newAxisId);
    }
  }
  // ★ 一轮结束：把本轮所有打断后的轴线片段写入全局 axistobedeal
  RTZ3State.axistobedeal.Clear();
  RTZ3State.axistobedeal.AddRange(finalAxisIds);

  // ★ 调试：把 axistobedeal 里的轴线全部标红，方便你确认
  DebugHighlightAxisToBeDeal(doc);
}

// ★ 收集打断参数的小工具
void AddSplitParam(
  Dictionary<Guid, List<double>> map,
  Guid id,
  double t,
  Interval domain,
  double tol)
{
  // 略放宽一点，先不在这里截端点，后面统一处理
  if (t < domain.T0 - tol || t > domain.T1 + tol)
    return;

  List<double> list;
  if (!map.TryGetValue(id, out list))
  {
    list = new List<double>();
    map[id] = list;
  }
  list.Add(t);
}

// ★ 把一根 AxisInfo（单根轴线片段）写入文档：Axis + 墙线 + 保温线 + Name
Guid CommitSingleAxisToDoc(
  RhinoDoc doc,
  AxisInfo info,
  int wallLayerIndex,
  int wallGlasLayerIndex,
  int wallStudLayerIndex,
  int axisLayerIndex,
  int insulLayerIndex)
{
  if (doc == null) return Guid.Empty;
  if (info == null || info.AxisCurve == null) return Guid.Empty;
  if (axisLayerIndex < 0) return Guid.Empty;

  string wallTag     = GetWallKindTag(info.WallType);
  string axisTypeTag = GetAxisTypeTag(info.AxisType);
  string startStr    = FormatPoint(info.Start);
  string endStr      = FormatPoint(info.End);

  // 1）Axis
  var axisAttr = new ObjectAttributes();
  axisAttr.LayerIndex = axisLayerIndex;
  axisAttr.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer;

  Guid axisId = doc.Objects.AddCurve(info.AxisCurve, axisAttr);

  var axisObj = doc.Objects.Find(axisId);
  if (axisObj != null)
  {
    var aAttr = axisObj.Attributes;

    string baseAxisName =
      $"Wallaxis|AxisId={axisId}" +
      $"|Axistype={axisTypeTag}" +
      $"|L={info.OffsetL}" +
      $"|R={info.OffsetR}" +
      $"|Walltype={wallTag}" +
      $"|InsulL={info.InsulLeftOffset}" +
      $"|InsulR={info.InsulRightOffset}" +
      $"|InsulLOn={(info.InsulLeftOn ? 1 : 0)}" +
      $"|InsulROn={(info.InsulRightOn ? 1 : 0)}" +
      $"|UseInsulLAsOutline={(info.UseInsulLeftAsOutline ? 1 : 0)}" +
      $"|UseInsulRAsOutline={(info.UseInsulRightAsOutline ? 1 : 0)}" +
      $"|Start={startStr}" +
      $"|End={endStr}";

    // ★ 1.3.3：继承旧轴线 Name 中非本脚本维护的额外 token（如 GeoId=...）
    try
    {
      var extra = NameCarryState.GetAxisExtras(info.SourceAxisId);
      baseAxisName = NameCarryState.AppendExtraTokens(baseAxisName, extra);
    }
    catch { }

    aAttr.Name = baseAxisName;
    doc.Objects.ModifyAttributes(axisObj, aAttr, true);
  }

  // 2）墙线
  int targetLayerIndex = -1;
  if (info.WallType == WallKind.Concrete || info.WallType == WallKind.Block)
    targetLayerIndex = wallLayerIndex;
  else if (info.WallType == WallKind.Glass)
    targetLayerIndex = wallGlasLayerIndex;
  else if (info.WallType == WallKind.Stud)
    targetLayerIndex = wallStudLayerIndex;

  if (targetLayerIndex >= 0)
  {
    if (info.WallLeft != null)
    {
      Point3d wlStart   = info.WallLeft.PointAtStart;
      Point3d wlEnd     = info.WallLeft.PointAtEnd;
      string  wlStartStr = FormatPoint(wlStart);
      string  wlEndStr   = FormatPoint(wlEnd);

      var wallAttrL = new ObjectAttributes();
      wallAttrL.LayerIndex = targetLayerIndex;
      string baseName_wallAttrL =
        $"Wall|AxisId={axisId}" +
        $"|Side=Left" +
        $"|Walltype={wallTag}" +
        $"|AxisStart={startStr}" +
        $"|AxisEnd={endStr}" +
        $"|Start={wlStartStr}" +
        $"|End={wlEndStr}";


      // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

      try

      {

        var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Wall", "Left");

        baseName_wallAttrL = NameCarryState.AppendExtraTokens(baseName_wallAttrL, extra);

      }

      catch { }


      wallAttrL.Name = baseName_wallAttrL;
      doc.Objects.AddCurve(info.WallLeft, wallAttrL);
    }

    if (info.WallRight != null)
    {
      Point3d wrStart   = info.WallRight.PointAtStart;
      Point3d wrEnd     = info.WallRight.PointAtEnd;
      string  wrStartStr = FormatPoint(wrStart);
      string  wrEndStr   = FormatPoint(wrEnd);

      var wallAttrR = new ObjectAttributes();
      wallAttrR.LayerIndex = targetLayerIndex;
      string baseName_wallAttrR =
        $"Wall|AxisId={axisId}" +
        $"|Side=Right" +
        $"|Walltype={wallTag}" +
        $"|AxisStart={startStr}" +
        $"|AxisEnd={endStr}" +
        $"|Start={wrStartStr}" +
        $"|End={wrEndStr}";


      // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

      try

      {

        var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Wall", "Right");

        baseName_wallAttrR = NameCarryState.AppendExtraTokens(baseName_wallAttrR, extra);

      }

      catch { }


      wallAttrR.Name = baseName_wallAttrR;
      doc.Objects.AddCurve(info.WallRight, wallAttrR);
    }

    // ★ 1.1.2：玻璃幕墙内墙线（Wallinside）落图（与外墙线同层）
    if (info.WallType == WallKind.Glass)
    {
      double tol = doc.ModelAbsoluteTolerance;

      // 防御性：若上游没算出来，这里补算一次
      if (info.WallinsideLeft == null || info.WallinsideRight == null)
      {
        Plane pl;
        if (BuildAxisPlane(info, out pl))
        {
          Curve wiL, wiR;
          BuildGlassInsideWallPair(info, pl, tol, out wiL, out wiR);
          info.WallinsideLeft  = wiL;
          info.WallinsideRight = wiR;
        }
      }

      if (info.WallinsideLeft != null)
      {
        Point3d wilStart    = info.WallinsideLeft.PointAtStart;
        Point3d wilEnd      = info.WallinsideLeft.PointAtEnd;
        string  wilStartStr = FormatPoint(wilStart);
        string  wilEndStr   = FormatPoint(wilEnd);

        var wallAttrIL = new ObjectAttributes();
        wallAttrIL.LayerIndex = targetLayerIndex;
        string baseName_wallAttrIL =
          $"Wallinside|AxisId={axisId}" +
          $"|Side=Left" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wilStartStr}" +
          $"|End={wilEndStr}";


        // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

        try

        {

          var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Wallinside", "Left");

          baseName_wallAttrIL = NameCarryState.AppendExtraTokens(baseName_wallAttrIL, extra);

        }

        catch { }


        wallAttrIL.Name = baseName_wallAttrIL;
        doc.Objects.AddCurve(info.WallinsideLeft, wallAttrIL);
      }

      if (info.WallinsideRight != null)
      {
        Point3d wirStart    = info.WallinsideRight.PointAtStart;
        Point3d wirEnd      = info.WallinsideRight.PointAtEnd;
        string  wirStartStr = FormatPoint(wirStart);
        string  wirEndStr   = FormatPoint(wirEnd);

        var wallAttrIR = new ObjectAttributes();
        wallAttrIR.LayerIndex = targetLayerIndex;
        string baseName_wallAttrIR =
          $"Wallinside|AxisId={axisId}" +
          $"|Side=Right" +
          $"|Walltype={wallTag}" +
          $"|AxisStart={startStr}" +
          $"|AxisEnd={endStr}" +
          $"|Start={wirStartStr}" +
          $"|End={wirEndStr}";


        // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

        try

        {

          var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Wallinside", "Right");

          baseName_wallAttrIR = NameCarryState.AppendExtraTokens(baseName_wallAttrIR, extra);

        }

        catch { }


        wallAttrIR.Name = baseName_wallAttrIR;
        doc.Objects.AddCurve(info.WallinsideRight, wallAttrIR);
      }
    }

  }

  // 3）保温外皮线
  if (insulLayerIndex >= 0)
  {
    if (info.InsulLeftOn && info.InsulLeftCurve != null)
    {
      Point3d ilStart   = info.InsulLeftCurve.PointAtStart;
      Point3d ilEnd     = info.InsulLeftCurve.PointAtEnd;
      string  ilStartStr = FormatPoint(ilStart);
      string  ilEndStr   = FormatPoint(ilEnd);

      var insAttrL = new ObjectAttributes();
      insAttrL.LayerIndex = insulLayerIndex;
      string baseName_insAttrL =
        $"Insulation|AxisId={axisId}" +
        $"|Side=Left" +
        $"|Offset={info.InsulLeftOffset}" +
        $"|Start={ilStartStr}" +
        $"|End={ilEndStr}";


      // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

      try

      {

        var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Insulation", "Left");

        baseName_insAttrL = NameCarryState.AppendExtraTokens(baseName_insAttrL, extra);

      }

      catch { }


      insAttrL.Name = baseName_insAttrL;
      doc.Objects.AddCurve(info.InsulLeftCurve, insAttrL);
    }

    if (info.InsulRightOn && info.InsulRightCurve != null)
    {
      Point3d irStart   = info.InsulRightCurve.PointAtStart;
      Point3d irEnd     = info.InsulRightCurve.PointAtEnd;
      string  irStartStr = FormatPoint(irStart);
      string  irEndStr   = FormatPoint(irEnd);

      var insAttrR = new ObjectAttributes();
      insAttrR.LayerIndex = insulLayerIndex;
      string baseName_insAttrR =
        $"Insulation|AxisId={axisId}" +
        $"|Side=Right" +
        $"|Offset={info.InsulRightOffset}" +
        $"|Start={irStartStr}" +
        $"|End={irEndStr}";


      // ★ 1.3.3：继承旧线 Name 中非本脚本维护的额外 token（如 GeoId=...）

      try

      {

        var extra = NameCarryState.GetCurveExtras(info.SourceAxisId, "Insulation", "Right");

        baseName_insAttrR = NameCarryState.AppendExtraTokens(baseName_insAttrR, extra);

      }

      catch { }


      insAttrR.Name = baseName_insAttrR;
      doc.Objects.AddCurve(info.InsulRightCurve, insAttrR);
    }
  }
  // ★ 新增：把新建的轴线 Id 返回
  return axisId;
}

// 在指定父层下，寻找同级图层名为 name 的图层索引（找不到返回 -1）
int FindSiblingLayerIndex(RhinoDoc doc, Guid parentId, string name)
{
  if (doc == null || string.IsNullOrEmpty(name)) return -1;

  for (int i = 0; i < doc.Layers.Count; i++)
  {
    Layer layer = doc.Layers[i];
    if (layer == null) continue;
    if (layer.ParentLayerId == parentId && layer.Name == name)
      return i;
  }
  return -1;
}

// 根据 AxisInfo 构建偏移平面：X 轴 = Start->End 方向，Y 轴 = 水平左侧
bool BuildAxisPlane(AxisInfo axis, out Plane plane)
{
  plane = Plane.WorldXY;
  Curve c = axis.AxisCurve;
  if (c == null) return false;

  double t0 = c.Domain.T0;
  Vector3d dir = c.TangentAt(t0);
  if (!dir.Unitize()) return false;

  Vector3d se = axis.End - axis.Start;
  if (!se.Unitize()) return false;
  if (Vector3d.Multiply(dir, se) < 0)
    dir.Reverse();

  Vector3d z = Vector3d.ZAxis;
  Vector3d y = Vector3d.CrossProduct(z, dir);
  if (!y.Unitize()) return false;

  plane = new Plane(axis.Start, dir, y);
  return true;
}

// 仅做预览：从轴线 + 平面生成左右偏移墙线，并画在 Display 上
void PreviewOffsetWalls(
  Curve axisCrv,
  Plane plane,
  double leftDist,
  double rightDist,
  double tol,
  Rhino.Display.DisplayPipeline display,
  SDColor leftColor,
  SDColor rightColor,
  WallKind wallKind = WallKind.Block)
{
  if (axisCrv == null) return;

  Curve wallL = null;
  Curve wallR = null;

  double L = leftDist;
  double R = rightDist;

  if (Math.Abs(L) > tol)
  {
    Curve[] offL = axisCrv.Offset(plane, -L, tol, CurveOffsetCornerStyle.Sharp);
    if (offL != null && offL.Length > 0 && offL[0] != null) wallL = offL[0];
  }
  else if (Math.Abs(L) <= tol && Math.Abs(R) > tol)
  {
    wallL = axisCrv.DuplicateCurve();
  }

  if (Math.Abs(R) > tol)
  {
    Curve[] offR = axisCrv.Offset(plane, R, tol, CurveOffsetCornerStyle.Sharp);
    if (offR != null && offR.Length > 0 && offR[0] != null) wallR = offR[0];
  }
  else if (Math.Abs(R) <= tol && Math.Abs(L) > tol)
  {
    wallR = axisCrv.DuplicateCurve();
  }

  if (wallL != null) display.DrawCurve(wallL, leftColor);
  if (wallR != null) display.DrawCurve(wallR, rightColor);

  // ★ 1.1.2：玻璃幕墙增加内墙线预览（Wallinside）
  if (wallKind == WallKind.Glass)
  {
    double sL = -leftDist;  // 左外墙线 signed offset
    double sR = rightDist;  // 右外墙线 signed offset

    double s1 = sL + (sR - sL) / 3.0;
    double s2 = sL + (sR - sL) * 2.0 / 3.0;

    Curve wiL = null;
    Curve wiR = null;

    if (Math.Abs(s1) <= tol) wiL = axisCrv.DuplicateCurve();
    else
    {
      var off = axisCrv.Offset(plane, s1, tol, CurveOffsetCornerStyle.Sharp);
      if (off != null && off.Length > 0 && off[0] != null) wiL = off[0];
    }

    if (Math.Abs(s2) <= tol) wiR = axisCrv.DuplicateCurve();
    else
    {
      var off = axisCrv.Offset(plane, s2, tol, CurveOffsetCornerStyle.Sharp);
      if (off != null && off.Length > 0 && off[0] != null) wiR = off[0];
    }

    if (wiL != null) display.DrawCurve(wiL, leftColor);
    if (wiR != null) display.DrawCurve(wiR, rightColor);
  }
}

// 仅预览保温外皮线：在墙线外侧再偏一圈（总距离 = |L/R| + 保温 offset）
void PreviewInsulationWalls(
  Curve axisCrv,
  Plane plane,
  double offsetL,
  double offsetR,
  double insulL,
  double insulR,
  bool insulLOn,
  bool insulROn,
  double tol,
  Rhino.Display.DisplayPipeline display,
  SDColor insulLeftColor,
  SDColor insulRightColor)
{
  if (axisCrv == null) return;

  // 左侧：即便外轮廓勾选，保温线仍然从“墙线”向外再偏 insulL
  if (insulLOn && insulL > tol)
  {
    double totalL = offsetL + insulL;  // ★ 直接用 offsetL + insulL，不取绝对值
    Curve[] offInsL = axisCrv.Offset(plane, -totalL, tol, CurveOffsetCornerStyle.Sharp);
    if (offInsL != null && offInsL.Length > 0 && offInsL[0] != null)
      display.DrawCurve(offInsL[0], insulLeftColor);
  }

  // 右侧
  if (insulROn && insulR > tol)
  {
    double totalR = offsetR + insulR;  // ★ 直接用 offsetR + insulR
    Curve[] offInsR = axisCrv.Offset(plane, totalR, tol, CurveOffsetCornerStyle.Sharp);
    if (offInsR != null && offInsR.Length > 0 && offInsR[0] != null)
      display.DrawCurve(offInsR[0], insulRightColor);
  }
}

// 单线变墙预览用的 DisplayConduit（不依赖 ComputeWallOffsets 等函数）
class RTZ3SingleToWallPreviewConduit : Rhino.Display.DisplayConduit
{
  readonly RhinoDoc      _doc;
  readonly ThicknessPanel _panel;

  // 要预览的轴线曲线
  public List<Curve> AxisCurves = new List<Curve>();

  public RTZ3SingleToWallPreviewConduit(RhinoDoc doc, ThicknessPanel panel)
  {
    _doc   = doc;
    _panel = panel;
  }

  protected override void CalculateBoundingBox(Rhino.Display.CalculateBoundingBoxEventArgs e)
  {
    if (AxisCurves == null) return;

    foreach (var crv in AxisCurves)
    {
      if (crv == null || !crv.IsValid) continue;
      var box = crv.GetBoundingBox(true);
      if (box.IsValid)
        e.IncludeBoundingBox(box);
    }
  }

  protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
  {
    if (_doc == null || _panel == null) return;
    if (AxisCurves == null || AxisCurves.Count == 0) return;

    var display = e.Display;
    double tol  = _doc.ModelAbsoluteTolerance;

    // ===== 从面板读取当前参数 =====
    double a  = _panel.LeftValue;
    double b  = _panel.RightValue;
    double ai = _panel.InsulLeftValue;
    double bi = _panel.InsulRightValue;

    bool insulLOn = _panel.InsulLeftOn;
    bool insulROn = _panel.InsulRightOn;
    bool outlineL = _panel.UseInsulLeftAsOutline;
    bool outlineR = _panel.UseInsulRightAsOutline;

    // ===== 计算墙线偏移（等价于 ComputeWallOffsets）=====
    double leftDist  = a;
    double rightDist = b;

    // 左保温作为外轮廓：L = a - ai, R = b + ai
    if (outlineL && !outlineR)
    {
      double aiForWall = insulLOn ? ai : 0.0;
      leftDist  = a - aiForWall;
      rightDist = b + aiForWall;
    }
    // 右保温作为外轮廓：L = a + bi, R = b - bi
    else if (outlineR && !outlineL)
    {
      double biForWall = insulROn ? bi : 0.0;
      leftDist  = a + biForWall;
      rightDist = b - biForWall;
    }
    // 都不勾 / 都勾：保持 a / b 不变

    // ===== 预览用颜色（和面板 L/R 配色一致就行）=====
    var wallLeftColor   = System.Drawing.Color.FromArgb(201, 255, 155);
    var wallRightColor  = System.Drawing.Color.FromArgb(255, 144, 173);
    var insulLeftColor  = System.Drawing.Color.FromArgb(173, 94, 94);
    var insulRightColor = System.Drawing.Color.FromArgb(173, 94, 94);

    // ===== 对每一根轴线做墙线 + 保温预览 =====
    foreach (var axisCrv in AxisCurves)
    {
      if (axisCrv == null || !axisCrv.IsValid) continue;

      // ---- 等价于 BuildAxisPlane(...) ----
      Point3d start = axisCrv.PointAtStart;
      Point3d end   = axisCrv.PointAtEnd;

      Vector3d dir = end - start;
      if (!dir.Unitize()) continue;

      Vector3d z = Vector3d.ZAxis;
      Vector3d y = Vector3d.CrossProduct(z, dir);
      if (!y.Unitize()) continue;

      Plane pl = new Plane(start, dir, y);

      // ---- 墙线偏移（等价于 BuildOffsetWallPair）----
      Curve wallL = null;
      Curve wallR = null;

      // 左线
      if (Math.Abs(leftDist) > tol)
      {
        var offL = axisCrv.Offset(pl, -leftDist, tol, CurveOffsetCornerStyle.Sharp);
        if (offL != null && offL.Length > 0 && offL[0] != null)
          wallL = offL[0];
      }
      else if (Math.Abs(leftDist) <= tol && Math.Abs(rightDist) > tol)
      {
        // 只有一侧有厚度时，另一侧复制轴线
        wallL = axisCrv.DuplicateCurve();
      }

      // 右线
      if (Math.Abs(rightDist) > tol)
      {
        var offR = axisCrv.Offset(pl, rightDist, tol, CurveOffsetCornerStyle.Sharp);
        if (offR != null && offR.Length > 0 && offR[0] != null)
          wallR = offR[0];
      }
      else if (Math.Abs(rightDist) <= tol && Math.Abs(leftDist) > tol)
      {
        wallR = axisCrv.DuplicateCurve();
      }

      if (wallL != null) display.DrawCurve(wallL, wallLeftColor);
      if (wallR != null) display.DrawCurve(wallR, wallRightColor);

      // ---- 保温线偏移（等价于 BuildInsulationPair）----
      // 左保温：总偏移 = leftDist + ai
      if (insulLOn && ai > tol)
      {
        double totalL = leftDist + ai;
        Curve[] offInsL;

        if (Math.Abs(totalL) < tol)
        {
          offInsL = new Curve[] { axisCrv.DuplicateCurve() };
        }
        else
        {
          offInsL = axisCrv.Offset(pl, -totalL, tol, CurveOffsetCornerStyle.Sharp);
        }

        if (offInsL != null && offInsL.Length > 0 && offInsL[0] != null)
          display.DrawCurve(offInsL[0], insulLeftColor);
      }

      // 右保温：总偏移 = rightDist + bi
      if (insulROn && bi > tol)
      {
        double totalR = rightDist + bi;
        Curve[] offInsR;

        if (Math.Abs(totalR) < tol)
        {
          offInsR = new Curve[] { axisCrv.DuplicateCurve() };
        }
        else
        {
          offInsR = axisCrv.Offset(pl, totalR, tol, CurveOffsetCornerStyle.Sharp);
        }

        if (offInsR != null && offInsR.Length > 0 && offInsR[0] != null)
          display.DrawCurve(offInsR[0], insulRightColor);
      }
    }
  }
}

// 实际生成左右墙线（用于写入 PB-Wall / PB-Wall-Glas / PB-Wall-Stud）
void BuildOffsetWallPair(
  AxisInfo axis,
  Plane plane,
  double tol,
  out Curve wallL,
  out Curve wallR)
{
  wallL = null;
  wallR = null;

  Curve axisCrv = axis.AxisCurve;
  if (axisCrv == null) return;

  double L = axis.OffsetL;
  double R = axis.OffsetR;

  if (Math.Abs(L) > tol)
  {
    Curve[] offL = axisCrv.Offset(plane, -L, tol, CurveOffsetCornerStyle.Sharp);
    if (offL != null && offL.Length > 0 && offL[0] != null) wallL = offL[0];
  }
  else if (Math.Abs(L) <= tol && Math.Abs(R) > tol)
  {
    wallL = axisCrv.DuplicateCurve();
  }

  if (Math.Abs(R) > tol)
  {
    Curve[] offR = axisCrv.Offset(plane, R, tol, CurveOffsetCornerStyle.Sharp);
    if (offR != null && offR.Length > 0 && offR[0] != null) wallR = offR[0];
  }
  else if (Math.Abs(R) <= tol && Math.Abs(L) > tol)
  {
    wallR = axisCrv.DuplicateCurve();
  }
}

// ★ 1.1.2：仅玻璃幕墙使用：生成两根“内墙线”（Wallinside），把墙厚三等分
// 说明：
// - 外墙线偏移量：左 = -OffsetL，右 = +OffsetR（OffsetL/OffsetR 允许为负）
// - 内墙线偏移量：在 [左外墙线偏移, 右外墙线偏移] 上做 1/3、2/3 插值
void BuildGlassInsideWallPair(
  AxisInfo axis,
  Plane plane,
  double tol,
  out Curve wallInsideL,
  out Curve wallInsideR)
{
  wallInsideL = null;
  wallInsideR = null;
  if (axis == null) return;
  if (axis.AxisCurve == null) return;

  Curve axisCrv = axis.AxisCurve;

  double sL = -axis.OffsetL;   // 左外墙线的 signed offset
  double sR =  axis.OffsetR;   // 右外墙线的 signed offset

  // 4 根线把墙厚均分 3 层
  double s1 = sL + (sR - sL) / 3.0;
  double s2 = sL + (sR - sL) * 2.0 / 3.0;

  Curve OffsetOrDup(double dist)
  {
    if (Math.Abs(dist) <= tol)
      return axisCrv.DuplicateCurve();

    var off = axisCrv.Offset(plane, dist, tol, CurveOffsetCornerStyle.Sharp);
    if (off != null && off.Length > 0 && off[0] != null && off[0].IsValid)
      return off[0];

    return axisCrv.DuplicateCurve();
  }

  wallInsideL = OffsetOrDup(s1);
  wallInsideR = OffsetOrDup(s2);
}


// 实际生成保温外皮线：总距离 = |L/R| + 保温 offset
void BuildInsulationPair(
  AxisInfo axis,
  Plane plane,
  double tol,
  out Curve insulL,
  out Curve insulR)
{
  insulL = null;
  insulR = null;

  Curve axisCrv = axis.AxisCurve;
  if (axisCrv == null) return;

  double L = axis.OffsetL;
  double R = axis.OffsetR;

  // 左侧
  if (axis.InsulLeftOn && axis.InsulLeftOffset > 0)
  {
    double totalL = L + axis.InsulLeftOffset;
    Curve[] offInsL;

    // 如果总偏移非常小（接近 0），直接复制轴线
    if (Math.Abs(totalL) < tol)
      offInsL = new Curve[] { axisCrv.DuplicateCurve() };
    else
      offInsL = axisCrv.Offset(plane, -totalL, tol, CurveOffsetCornerStyle.Sharp);

    if (offInsL != null && offInsL.Length > 0 && offInsL[0] != null)
      insulL = offInsL[0];
  }

  // 右侧
  if (axis.InsulRightOn && axis.InsulRightOffset > 0)
  {
    double totalR = R + axis.InsulRightOffset;
    Curve[] offInsR;

    if (Math.Abs(totalR) < tol)
      offInsR = new Curve[] { axisCrv.DuplicateCurve() };
    else
      offInsR = axisCrv.Offset(plane, totalR, tol, CurveOffsetCornerStyle.Sharp);

    if (offInsR != null && offInsR.Length > 0 && offInsR[0] != null)
      insulR = offInsR[0];
  }
}

// ★ 工具：把一根曲线“炸开 → 光顺段再 Join → 在折线拐角处 Split”
// 返回的每一段都是内部 C1 连续的曲线，拐角处保持断开
List<Curve> ExplodeAndJoinSmooth(Curve raw, double tol)
{
  var segments = new List<Curve>();
  if (raw == null) return segments;

  // 1）先炸开：PolyCurve / PolylineCurve → 单段
  var poly = raw as PolyCurve;
  if (poly != null)
  {
    var pieces = poly.DuplicateSegments();
    if (pieces != null)
    {
      foreach (var p in pieces)
      {
        if (p != null && p.IsValid && !p.IsShort(tol))
          segments.Add(p);
      }
    }
  }
  else if (raw is PolylineCurve plc)
  {
    var pieces = plc.DuplicateSegments();
    if (pieces != null)
    {
      foreach (var p in pieces)
      {
        if (p != null && p.IsValid && !p.IsShort(tol))
          segments.Add(p);
      }
    }
  }
  else
  {
    var dup = raw.DuplicateCurve();
    if (dup != null && dup.IsValid && !dup.IsShort(tol))
      segments.Add(dup);
  }

  if (segments.Count == 0)
    return segments;

  // 2）粗 Join 一遍（相接的全部连在一起）
  var joined = Curve.JoinCurves(segments, tol);
  if (joined == null || joined.Length == 0)
    return segments; // join 失败就用炸开的段

  var result = new List<Curve>();

  // 3）对每一根 Joined 曲线：在“C1 不连续”的地方 Split
  foreach (var c in joined)
  {
    if (c == null || !c.IsValid)
      continue;

    var tList = new List<double>();
    double t0 = c.Domain.T0;
    double t1 = c.Domain.T1;
    double t;

    // C1_continuous 的不连续点 = 折线拐角 / 切向不连续
    while (c.GetNextDiscontinuity(Continuity.C1_continuous, t0, t1, out t))
    {
      // 略微远离端点，避免在起终点附近切一刀
      if (t > c.Domain.T0 + tol && t < c.Domain.T1 - tol)
        tList.Add(t);

      t0 = t;
    }

    if (tList.Count == 0)
    {
      if (!c.IsShort(tol))
        result.Add(c);
    }
    else
    {
      var pieces = c.Split(tList);
      if (pieces == null || pieces.Length == 0)
        continue;

      foreach (var p in pieces)
      {
        if (p != null && p.IsValid && !p.IsShort(tol))
          result.Add(p);
      }
    }
  }

  return result;
}

// 中文名称展示用（命令行提示）
string GetWallKindNameCn(WallKind kind)
{
  switch (kind)
  {
    case WallKind.Concrete: return "钢筋砼墙";
    case WallKind.Block:    return "填充墙";
    case WallKind.Glass:    return "玻璃幕墙";
    case WallKind.Stud:     return "轻质隔墙";
  }
  return "钢筋砼墙";
}

// 轴线 / 墙线 Name 中写入的英文 tag
string GetWallKindTag(WallKind kind)
{
  switch (kind)
  {
    case WallKind.Concrete: return "Concrete";
    case WallKind.Block:    return "block";
    case WallKind.Glass:    return "glass";
    case WallKind.Stud:     return "stud";
  }
  return "Concrete";
}

// ====== 8.1：墙体等级（高 -> 低） ======
// 1 RC(Concrete) > 2 填充(Block) > 3 轻质(Stud) > 4 玻璃(Glass)
int GetWallKindLevel(WallKind kind)
{
  switch (kind)
  {
    case WallKind.Concrete: return 4;
    case WallKind.Block:    return 3;
    case WallKind.Stud:     return 2;
    case WallKind.Glass:    return 1;
  }
  return 0;
}

WallKind ParseWallKindTag(string tag)
{
  if (string.IsNullOrWhiteSpace(tag)) return WallKind.Block;
  string t = tag.Trim();

  if (t.Equals("Concrete", StringComparison.OrdinalIgnoreCase)) return WallKind.Concrete;
  if (t.Equals("block",    StringComparison.OrdinalIgnoreCase)) return WallKind.Block;
  if (t.Equals("stud",     StringComparison.OrdinalIgnoreCase)) return WallKind.Stud;
  if (t.Equals("glass",    StringComparison.OrdinalIgnoreCase)) return WallKind.Glass;

  return WallKind.Block;
}


// 轴线 / 墙线 Name 中写入的英文 tag
string GetAxisTypeTag(AxisCurveKind kind)
{
  switch (kind)
  {
    case AxisCurveKind.Line:   return "line";
    case AxisCurveKind.Arc:    return "arc";
    case AxisCurveKind.Spline: return "spline";
  }
  return "line";
}

// 坐标格式化为 "x,y,z"（3 位小数，使用 InvariantCulture，方便字符串解析）
string FormatPoint(Point3d p)
{
  var c = CultureInfo.InvariantCulture;
  return string.Format(c, "{0:F3},{1:F3},{2:F3}", p.X, p.Y, p.Z);
}

// ★ 新增：从 "Wall|AxisId=xxx|..." 或 "Insulation|AxisId=xxx|..." 里解析出 AxisId
bool TryParseAxisIdFromName(string name, out Guid axisId)
{
  axisId = Guid.Empty;
  if (string.IsNullOrEmpty(name))
    return false;

  // 只匹配 “|AxisId=...|” 或 “开头AxisId=...|”
  // 避免误命中 OldAxisId= / SomethingAxisId=
  var m = System.Text.RegularExpressions.Regex.Match(
    name,
    @"(?i)(?:^|\|)\s*AxisId\s*=\s*([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");

  if (!m.Success) return false;
  return Guid.TryParse(m.Groups[1].Value, out axisId);
}

// ★ 新增：从 "...|Start=x,y,z|..." 或 "...|End=x,y,z|..." 里解析出点坐标
bool TryParsePointFromName(string name, string key, out Point3d pt)
{
  pt = Point3d.Unset;
  if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
    return false;

  string token = key + "="; // key = "Start" / "End"
  int idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
  if (idx < 0)
    return false;

  int start = idx + token.Length;
  int end   = name.IndexOf('|', start);
  string val = (end >= 0)
    ? name.Substring(start, end - start)
    : name.Substring(start);

  if (string.IsNullOrEmpty(val))
    return false;

  var parts = val.Split(',');
  if (parts.Length < 3)
    return false;

  var c = CultureInfo.InvariantCulture;
  double x, y, z;
  if (!double.TryParse(parts[0], NumberStyles.Float, c, out x)) return false;
  if (!double.TryParse(parts[1], NumberStyles.Float, c, out y)) return false;
  if (!double.TryParse(parts[2], NumberStyles.Float, c, out z)) return false;

  pt = new Point3d(x, y, z);
  return true;
}

//refresh 按钮的工具函数：端点索引
struct PtKey : IEquatable<PtKey>
{
  public long X, Y, Z;
  public PtKey(long x, long y, long z) { X = x; Y = y; Z = z; }

  public static PtKey FromPoint(Point3d p, double grid)
  {
    if (grid <= 0) grid = 1e-6;  // ★ 兜底，防止除0/极小grid
    return new PtKey(
    (long)Math.Round(p.X / grid),
    (long)Math.Round(p.Y / grid),
    (long)Math.Round(p.Z / grid));
  }

  public bool Equals(PtKey other) => X == other.X && Y == other.Y && Z == other.Z;
  public override bool Equals(object obj) => obj is PtKey k && Equals(k);

  public override int GetHashCode()
  {
    unchecked
    {
      int h = 17;
      h = h * 31 + X.GetHashCode();
      h = h * 31 + Y.GetHashCode();
      h = h * 31 + Z.GetHashCode();
      return h;
    }
  }
}

void IndexEndpoint(Dictionary<PtKey, HashSet<Guid>> map, Point3d pt, Guid axisId, double grid)
{
  var k = PtKey.FromPoint(pt, grid);
  if (!map.TryGetValue(k, out var set))
  {
    set = new HashSet<Guid>();
    map[k] = set;
  }
  set.Add(axisId);
}

IEnumerable<Guid> QueryNearEndpoint(Dictionary<PtKey, HashSet<Guid>> map, Point3d pt, double grid)
{
  if (map == null) yield break;
  if (grid <= 0) grid = 1e-6;
  var baseK = PtKey.FromPoint(pt, grid);

  for (long dx = -1; dx <= 1; dx++)
  for (long dy = -1; dy <= 1; dy++)
  for (long dz = -1; dz <= 1; dz++)
  {
    var k = new PtKey(baseK.X + dx, baseK.Y + dy, baseK.Z + dz);
    if (map.TryGetValue(k, out var set))
    {
      foreach (var id in set)
        yield return id;
    }
  }
}
//refresh 按钮的工具函数⬆⬆⬆⬆⬆⬆

// ===== 新增：根据 a,b,ai,bi 和左右“保温线作为外轮廓”勾选，算出最终墙线偏移 =====
// a  = 左墙厚度（面板 L）
// b  = 右墙厚度（面板 R）
// ai = 左保温厚度（只有 InsulLeftOn 时才传进来非零）
// bi = 右保温厚度（只有 InsulRightOn 时才传进来非零）
void ComputeWallOffsets(
  double a,  double b,
  double ai, double bi,
  bool useOutlineL,
  bool useOutlineR,
  out double leftDist,
  out double rightDist)
{
  // 默认 = 原始墙厚
  leftDist  = a;
  rightDist = b;

  // 左保温作为外轮廓：L = a - ai, R = b + ai
  if (useOutlineL && !useOutlineR)
  {
    leftDist  = a - ai;
    rightDist = b + ai;
  }
  // 右保温作为外轮廓：L = a + bi, R = b - bi
  else if (useOutlineR && !useOutlineL)
  {
    leftDist  = a + bi;
    rightDist = b - bi;
  }
  // 都不勾 或 理论上同时勾：保持 a / b 不变
}

// ★ 简单的 BoundingBox 相交判断（闭区间判断）
bool BoundingBoxesIntersect(BoundingBox a, BoundingBox b)
{
  if (!a.IsValid || !b.IsValid)
    return false;

  if (a.Max.X < b.Min.X || a.Min.X > b.Max.X) return false;
  if (a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y) return false;
  if (a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z) return false;

  return true;
}

// 用于“几何求交”的更宽容差：
// - 至少是 absTol * 10
// - 并且至少等于 0.1mm（按文档单位换算）
double GetAxisIntersectionTol(RhinoDoc doc)
{
  double absTol = (doc != null) ? doc.ModelAbsoluteTolerance : 0.001;

  double mmToDoc = 1.0;
  try
  {
    // 把 1mm 换算成文档单位
    mmToDoc = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  }
  catch { /* 兜底 */ }

  double minTol = 0.1 * mmToDoc;     // 0.1mm
  return Math.Max(absTol * 10.0, minTol);
}

// CurveCurve 没命中时的兜底：端点“贴近”也算有关联（非常省）
// 只在 bbox 已经相交、且 CurveCurve 返回 0 时才调用
bool CurvesCloseAtEndpoints(Curve a, Curve b, double tol)
{
  if (a == null || b == null) return false;

  Point3d[] aPts = { a.PointAtStart, a.PointAtEnd };
  foreach (var p in aPts)
  {
    double t;
    if (b.ClosestPoint(p, out t, tol))
    {
      var q = b.PointAt(t);
      if (p.DistanceTo(q) <= tol) return true;
    }
  }

  Point3d[] bPts = { b.PointAtStart, b.PointAtEnd };
  foreach (var p in bPts)
  {
    double t;
    if (a.ClosestPoint(p, out t, tol))
    {
      var q = a.PointAt(t);
      if (p.DistanceTo(q) <= tol) return true;
    }
  }

  return false;
}


// ★ 调试用小工具：每一轮先恢复所有 Wallaxis 颜色，再把 axistobedeal 里的轴线标红
void DebugHighlightAxisToBeDeal(RhinoDoc doc)
{
  if (doc == null) return;

  // 0）先把所有轴线（Name 以 Wallaxis 开头）的颜色恢复成“来自图层”
  var all = doc.Objects.FindByObjectType(ObjectType.Curve);
  if (all != null && all.Length > 0)
  {
    foreach (var obj in all)
    {
      if (obj == null) continue;

      string name = obj.Attributes.Name;
      if (string.IsNullOrEmpty(name))
        continue;

      if (!name.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
        continue;

      var attr = obj.Attributes.Duplicate();
      if (attr.ColorSource != ObjectColorSource.ColorFromLayer)
      {
        attr.ColorSource = ObjectColorSource.ColorFromLayer;
        // ObjectColor 可以不改，来自图层即可
        doc.Objects.ModifyAttributes(obj, attr, true);
      }
    }
  }

  // 1）再标红本轮 axistobedeal 里的轴线
  var list = RTZ3State.axistobedeal;
  if (list == null || list.Count == 0)
  {
    RhinoApp.WriteLine(" axistobedeal 为空，本轮没有轴线需要标红。");
    doc.Views.Redraw();
    return;
  }

  int hit = 0;
  foreach (var id in list)
  {
    var obj = doc.Objects.Find(id) as CurveObject;
    if (obj == null)
      continue;

    var attr = obj.Attributes.Duplicate();
    attr.ColorSource = ObjectColorSource.ColorFromObject;
    attr.ObjectColor = SDColor.Red;

    if (doc.Objects.ModifyAttributes(obj, attr, true))
      hit++;
  }

  RhinoApp.WriteLine("本轮 axistobedeal 中共有 {0} 条轴线已被标红。", hit);
  doc.Views.Redraw();
}

// ★ 工具：单线变墙完成后，删除本轮用户选中的原始曲线
void DeleteSourceCurvesForSingleToWall(RhinoDoc doc, List<Guid> ids)
{
  if (doc == null) return;
  if (ids == null || ids.Count == 0) return;

  foreach (var id in ids)
  {
    if (id == Guid.Empty)
      continue;

    var obj = doc.Objects.FindId(id);
    if (obj == null)
      continue;

    // 不做任何 Name / AxisId / 坐标判断，用户选了就删
    doc.Objects.Delete(obj, true);
  }
}

// ★ 单线变墙专用：
//   对一批“已炸开的输入曲线段”进行：
//   1）按端点聚类，
//   2）只在“恰好两条曲线”且“切线共线 + 曲率相同”的地方建立连接，
//   3）把这些平滑连接的段按连通分量 Join 一次，
//   4）Join 完对每条曲线做一次 Simplify（和 JoinSmoothAxisCurvesAtEndpoints 同风格），
//   返回可以直接当作轴线用的曲线列表。
List<Curve> JoinSmoothSegmentsForSingleToWall(List<Curve> segments, double tol)
{
  var result = new List<Curve>();

  if (segments == null || segments.Count == 0)
    return result;

  // 1）收集所有端点
  var ends = new List<SingleAxisEndRef>();

  for (int i = 0; i < segments.Count; i++)
  {
    var crv = segments[i];
    if (crv == null || !crv.IsValid)
      continue;

    Interval d = crv.Domain;
    double t0 = d.T0;
    double t1 = d.T1;

    Point3d p0 = crv.PointAt(t0);
    Point3d p1 = crv.PointAt(t1);

    ends.Add(new SingleAxisEndRef
    {
      Index     = i,
      Curve     = crv,
      Parameter = t0,
      Point     = p0
    });

    ends.Add(new SingleAxisEndRef
    {
      Index     = i,
      Curve     = crv,
      Parameter = t1,
      Point     = p1
    });
  }

  if (ends.Count < 2)
  {
    // 什么都没法合并，就把原曲线拷一份返回
    foreach (var c in segments)
    {
      if (c != null && c.IsValid)
        result.Add(c.DuplicateCurve());
    }
    return result;
  }

  // 2）按坐标聚类端点：距离 <= tol 视为同一簇
  var centers = new List<Point3d>();
  var groups  = new List<List<int>>(); // 存的是 ends 里的下标
  double tol2 = tol * tol;

  for (int i = 0; i < ends.Count; i++)
  {
    var end = ends[i];
    bool added = false;

    for (int g = 0; g < centers.Count; g++)
    {
      if (end.Point.DistanceToSquared(centers[g]) <= tol2)
      {
        groups[g].Add(i);
        added = true;
        break;
      }
    }

    if (!added)
    {
      centers.Add(end.Point);
      groups.Add(new List<int> { i });
    }
  }

  if (groups.Count == 0)
  {
    foreach (var c in segments)
    {
      if (c != null && c.IsValid)
        result.Add(c.DuplicateCurve());
    }
    return result;
  }

  // 3）在“恰好有 2 个端点”的簇里，用切线 + 曲率判断是否几何连续
  var adj = new Dictionary<int, HashSet<int>>(); // index → 邻接 index 集合

  double angleTol        = Math.PI / 180.0 * 1.0; // 1°
  double curvatureTolAbs = 1e-6;                  // 绝对曲率容差
  double curvatureTolRel = 1e-3;                  // 相对曲率容差 0.1%

  void AddEdge(int a, int b)
  {
    HashSet<int> set;
    if (!adj.TryGetValue(a, out set))
    {
      set = new HashSet<int>();
      adj[a] = set;
    }
    set.Add(b);
  }

  foreach (var group in groups)
  {
    // 只处理“刚好 2 个端点”的情况：
    //   - 1 个：没相交，直接忽略；
    //   - ≥3 个：十字 / Y / 多线交，按你的要求不在这里 join。
    if (group.Count != 2)
      continue;

    var e0 = ends[group[0]];
    var e1 = ends[group[1]];

    // 同一条曲线自己的两个端点（闭合）跳过
    if (e0.Index == e1.Index)
      continue;

    Curve cA = e0.Curve;
    Curve cB = e1.Curve;
    if (cA == null || cB == null)
      continue;

    double tA = e0.Parameter;
    double tB = e1.Parameter;

    // 切线方向
    Vector3d tanA = cA.TangentAt(tA);
    Vector3d tanB = cB.TangentAt(tB);
    if (!tanA.Unitize() || !tanB.Unitize())
      continue;

    double ang1 = Vector3d.VectorAngle(tanA, tanB);
    double ang2 = Vector3d.VectorAngle(tanA, -tanB);
    double ang  = Math.Min(ang1, ang2);

    // 切线不共线 → 折角 / T / Y，不能算连续轴线
    if (ang > angleTol)
      continue;

    // 曲率
    Vector3d curvA = cA.CurvatureAt(tA);
    Vector3d curvB = cB.CurvatureAt(tB);

    double kA    = curvA.Length;
    double kB    = curvB.Length;
    double maxK  = Math.Max(kA, kB);
    bool  kEqual = false;

    if (maxK <= curvatureTolAbs)
    {
      // 两边都近似直线（曲率≈0）
      kEqual = true;
    }
    else
    {
      double diffK = Math.Abs(kA - kB);
      if (diffK / maxK <= curvatureTolRel)
        kEqual = true;
    }

    if (!kEqual)
      continue;

    // 满足“切线共线 + 曲率相同”，视为在该点几何连续
    AddEdge(e0.Index, e1.Index);
    AddEdge(e1.Index, e0.Index);
  }

  // 4）按“连通分量”一组组 Join + Simplify
  var visited = new HashSet<int>();
  var inComponent = new HashSet<int>(); // 记录哪些 index 已经落入某个组件里

  for (int i = 0; i < segments.Count; i++)
  {
    if (!adj.ContainsKey(i))
      continue; // 这个 index 根本没有任何“平滑连接”的邻居

    if (visited.Contains(i))
      continue;

    var comp  = new List<int>();
    var queue = new Queue<int>();
    queue.Enqueue(i);
    visited.Add(i);

    while (queue.Count > 0)
    {
      int cur = queue.Dequeue();
      comp.Add(cur);

      HashSet<int> neigh;
      if (!adj.TryGetValue(cur, out neigh))
        continue;

      foreach (var nb in neigh)
      {
        if (visited.Add(nb))
          queue.Enqueue(nb);
      }
    }

    if (comp.Count < 2)
      continue; // 理论上不会发生，但保险起见

    // 收集这一组需要 Join 的曲线
    var toJoin = new List<Curve>();
    foreach (int idx in comp)
    {
      var c = segments[idx];
      if (c == null || !c.IsValid)
        continue;

      toJoin.Add(c.DuplicateCurve());
      inComponent.Add(idx);
    }

    if (toJoin.Count < 2)
      continue;

    Curve[] joined = Curve.JoinCurves(toJoin, tol);
    if (joined == null || joined.Length == 0)
      continue;

    foreach (var j in joined)
    {
      if (j == null || !j.IsValid)
        continue;

      // ★ 关键：完全照搬 JoinSmoothAxisCurvesAtEndpoints 的写法
      //   只做一次普通 Simplify，不再自己 TryGetArc / 重建圆弧
      Curve simplified = j.Simplify(
        CurveSimplifyOptions.All,
        tol,
        Math.PI / 180.0 * 1.0   // 1° 角度容差
      );

      Curve finalCurve = (simplified != null && simplified.IsValid)
        ? simplified
        : j;

      result.Add(finalCurve);
    }
  }

  // 5）把那些没有参与任何“平滑 Join”的段原样带进去
  for (int i = 0; i < segments.Count; i++)
  {
    var c = segments[i];
    if (c == null || !c.IsValid)
      continue;

    // 已经被某个组件吃掉了，就不要再重复加原始段
    if (inComponent.Contains(i))
      continue;

    result.Add(c.DuplicateCurve());
  }

  return result;
}


// ====== 1) signature：只用于“参数一致分组”，不参与几何 join 判断 ======
string GetWallaxisSignature(string name)
{
  if (string.IsNullOrWhiteSpace(name)) return "";
  var parts = name.Split('|');
  var kept = new List<string>();

  foreach (var raw in parts)
  {
    var s = (raw ?? "").Trim();
    if (s.Length == 0) continue;

    if (s.Equals("Wallaxis", StringComparison.OrdinalIgnoreCase))
    { kept.Add("wallaxis"); continue; }

    if (Guid.TryParse(s, out _)) continue;

    var sl = s.ToLowerInvariant();
    if (sl.StartsWith("id=") || sl.StartsWith("axisid=")) continue;
    if (sl.StartsWith("start")) continue;
    if (sl.StartsWith("end")) continue;
    if (sl.StartsWith("axistype") || sl.StartsWith("axis_type") || sl.StartsWith("type=")) continue;

    kept.Add(sl);
  }
  return string.Join("|", kept);
}

void ReplaceAxisCurveKeepName(RhinoDoc doc, Guid axisId, Curve newCrv, string keepName)
{
  if (doc == null) return;
  if (newCrv == null || !newCrv.IsValid) return;

  doc.Objects.Replace(axisId, newCrv);

  var obj2 = doc.Objects.FindId(axisId) as CurveObject;
  if (obj2 != null)
  {
    var a = obj2.Attributes;
    a.Name = keepName ?? (a.Name ?? "");
    doc.Objects.ModifyAttributes(obj2, a, true);
  }
}

// ====== 2) 端点引用（用于建图） ======
class AxisEndRef
{
  public int Index;
  public Curve Curve;
  public double Parameter;
  public Point3d Point;
}

// ====== 3) 计算“需要 join 的连通分量”（返回一堆 index 列表）======
// 只会把“端点簇=2 且 切线共线 且 曲率匹配”的边加入图里
List<List<int>> BuildSmoothJoinComponents(List<Curve> segments, double tol)
{
  var comps = new List<List<int>>();
  if (segments == null || segments.Count < 2) return comps;

  var ends = new List<AxisEndRef>();
  for (int i = 0; i < segments.Count; i++)
  {
    var crv = segments[i];
    if (crv == null || !crv.IsValid || crv.IsShort(tol)) continue;

    var d = crv.Domain;
    double t0 = d.T0, t1 = d.T1;

    ends.Add(new AxisEndRef { Index=i, Curve=crv, Parameter=t0, Point=crv.PointAt(t0) });
    ends.Add(new AxisEndRef { Index=i, Curve=crv, Parameter=t1, Point=crv.PointAt(t1) });
  }
  if (ends.Count < 2) return comps;

  // 端点聚类：<=tol 一簇
  var centers = new List<Point3d>();
  var groups  = new List<List<int>>();
  double tol2 = tol * tol;

  for (int i = 0; i < ends.Count; i++)
  {
    var e = ends[i];
    bool added = false;
    for (int g = 0; g < centers.Count; g++)
    {
      if (e.Point.DistanceToSquared(centers[g]) <= tol2)
      {
        groups[g].Add(i);
        added = true;
        break;
      }
    }
    if (!added)
    {
      centers.Add(e.Point);
      groups.Add(new List<int> { i });
    }
  }

  // 建图：index -> neighbors
  var adj = new Dictionary<int, HashSet<int>>();
  void AddEdge(int a, int b)
  {
    if (!adj.TryGetValue(a, out var set))
    {
      set = new HashSet<int>();
      adj[a] = set;
    }
    set.Add(b);
  }

  double angleTol        = Math.PI / 180.0 * 1.0; // 1°
  double curvatureTolAbs = 1e-6;
  double curvatureTolRel = 1e-3;

  foreach (var grp in groups)
  {
    // 只允许“恰好2端点”的簇参与 join（>=3 是十字/Y/T节点，禁止 join）
    if (grp.Count != 2) continue;

    var e0 = ends[grp[0]];
    var e1 = ends[grp[1]];

    if (e0.Index == e1.Index) continue;

    var cA = e0.Curve; var cB = e1.Curve;
    if (cA == null || cB == null) continue;

    double tA = e0.Parameter, tB = e1.Parameter;

    Vector3d tanA = cA.TangentAt(tA);
    Vector3d tanB = cB.TangentAt(tB);
    if (!tanA.Unitize() || !tanB.Unitize()) continue;

    double ang = Math.Min(Vector3d.VectorAngle(tanA, tanB),
                          Vector3d.VectorAngle(tanA, -tanB));
    if (ang > angleTol) continue;

    Vector3d curvA = cA.CurvatureAt(tA);
    Vector3d curvB = cB.CurvatureAt(tB);
    double kA = curvA.Length, kB = curvB.Length;
    double maxK = Math.Max(kA, kB);

    bool kEqual;
    if (maxK <= curvatureTolAbs) kEqual = true;
    else kEqual = (Math.Abs(kA - kB) / maxK <= curvatureTolRel);

    if (!kEqual) continue;

    AddEdge(e0.Index, e1.Index);
    AddEdge(e1.Index, e0.Index);
  }

  // BFS 连通分量
  var visited = new HashSet<int>();
  for (int i = 0; i < segments.Count; i++)
  {
    if (!adj.ContainsKey(i)) continue;
    if (visited.Contains(i)) continue;

    var comp = new List<int>();
    var q = new Queue<int>();
    q.Enqueue(i);
    visited.Add(i);

    while (q.Count > 0)
    {
      int cur = q.Dequeue();
      comp.Add(cur);
      if (!adj.TryGetValue(cur, out var neigh)) continue;
      foreach (var nb in neigh)
        if (visited.Add(nb)) q.Enqueue(nb);
    }

    if (comp.Count >= 2)
      comps.Add(comp);
  }

  return comps;
}

// ====== 4) ✅ 最终：只删“真正 join 成功的 component 内其它轴线” ======
void SimplifyAxesInPlace(RhinoDoc doc, List<Guid> axisIds, double tol, out List<Guid> removedByJoin)
{
  removedByJoin = new List<Guid>();

  if (doc == null) return;
  if (axisIds == null || axisIds.Count == 0) return;

  // ★ 关键：用于分组的 signature 必须排除 AxisId/Start/End/Axistype/Cap 等“会变的字段”
  string GetJoinSignature(string name)
  {
    if (string.IsNullOrWhiteSpace(name)) return "";

    var parts = name.Split('|');
    var keep = new List<string>();

    foreach (var raw in parts)
    {
      var s = (raw ?? "").Trim();
      if (s.Length == 0) continue;

      if (s.Equals("Wallaxis", StringComparison.OrdinalIgnoreCase)) continue;

      // 这些字段会变，不能参与分组，否则 Refresh “时而能join时而不能”
      if (s.StartsWith("AxisId=",   StringComparison.OrdinalIgnoreCase)) continue;
      if (s.StartsWith("Start=",    StringComparison.OrdinalIgnoreCase)) continue;
      if (s.StartsWith("End=",      StringComparison.OrdinalIgnoreCase)) continue;
      if (s.StartsWith("Axistype=", StringComparison.OrdinalIgnoreCase)) continue;
      if (s.StartsWith("AxisType=", StringComparison.OrdinalIgnoreCase)) continue;

      // 封口相关也不要参与分组（join 后端点语义会变）
      if (s.StartsWith("Cap", StringComparison.OrdinalIgnoreCase)) continue;

      keep.Add(s);
    }

    keep.Sort(StringComparer.OrdinalIgnoreCase);
    return string.Join("|", keep);
  }

  // 0）先清理无效 id（防止后续误判“被删轴线”）
  axisIds.RemoveAll(id => doc.Objects.FindId(id) == null);

  // 收集有效 Wallaxis
  var recs = new List<(Guid id, Curve dup, string name, int layerIndex, string sig)>();
  foreach (var id in axisIds)
  {
    var co = doc.Objects.FindId(id) as CurveObject;
    if (co == null) continue;

    var crv = co.Geometry as Curve;
    if (crv == null || !crv.IsValid || crv.IsShort(tol)) continue;

    var n = co.Attributes.Name ?? "";
    if (string.IsNullOrWhiteSpace(n)) continue;
    if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase)) continue;

    recs.Add((id, crv.DuplicateCurve(), n, co.Attributes.LayerIndex, GetJoinSignature(n)));
  }

  if (recs.Count == 0)
  {
    axisIds.Clear();
    return;
  }

  // 临时解锁涉及到的图层（只解锁这些轴线所在层）
  var lockBackup = new Dictionary<int, bool>();
  void EnsureUnlocked(int li)
  {
    if (li < 0 || li >= doc.Layers.Count) return;
    if (lockBackup.ContainsKey(li)) return;
    var ly = doc.Layers[li];
    if (ly == null) return;
    lockBackup[li] = ly.IsLocked;
    if (ly.IsLocked)
    {
      ly.IsLocked = false;
      doc.Layers.Modify(ly, li, true);
    }
  }
  foreach (var r in recs) EnsureUnlocked(r.layerIndex);

  var alive = new HashSet<Guid>();
  var removedSet = new HashSet<Guid>();

  try
  {
    // ★ 稳定遍历顺序：避免“有时 join 有时不 join”的非确定性
    foreach (var g in recs
      .GroupBy(x => x.sig)
      .OrderBy(gr => gr.Key, StringComparer.OrdinalIgnoreCase))
    {
      var list = g.OrderBy(x => x.id).ToList();
      if (list.Count < 1) continue;

      var segs = new List<Curve>();
      for (int i = 0; i < list.Count; i++)
        segs.Add(list[i].dup);

      // comps：每个 comp 是 seg index 的集合
      var comps = BuildSmoothJoinComponents(segs, tol);
      if (comps != null)
      {
        comps.Sort((a, b) =>
        {
          int am = a != null && a.Count > 0 ? a.Min() : int.MaxValue;
          int bm = b != null && b.Count > 0 ? b.Min() : int.MaxValue;
          return am.CompareTo(bm);
        });
      }

      var used = new HashSet<int>(); // 被 join 成功吞掉的 index（含 keeper）

      if (comps != null)
      {
        foreach (var comp in comps)
        {
          if (comp == null || comp.Count < 2) continue;

          comp.Sort(); // 稳定

          // 组件里若有任何曲线无效就跳过
          var toJoin = new List<Curve>();
          bool ok = true;
          foreach (int idx in comp)
          {
            var c = segs[idx];
            if (c == null || !c.IsValid || c.IsShort(tol)) { ok = false; break; }
            toJoin.Add(c.DuplicateCurve());
          }
          if (!ok) continue;

          // Join：必须严格返回 1 条才允许删除其它轴线（安全第一）
          var joined = Curve.JoinCurves(toJoin, tol);
          if (joined == null || joined.Length != 1 || joined[0] == null || !joined[0].IsValid)
            continue;

          Curve j = joined[0].Simplify(CurveSimplifyOptions.All, tol, Math.PI / 180.0 * 1.0) ?? joined[0];

          Curve axisCrvNorm;
          GetAxisKindAndNormalize(j, tol, out axisCrvNorm);

          // keeper：选 component 里“最长原轴线”那根，保留其 AxisId
          int keepIdx = comp[0];
          double bestLen = segs[keepIdx].GetLength();
          for (int k = 1; k < comp.Count; k++)
          {
            int idx = comp[k];
            double len = segs[idx].GetLength();
            if (len > bestLen) { bestLen = len; keepIdx = idx; }
          }

          var keeper = list[keepIdx];

          ReplaceAxisCurveKeepName(doc, keeper.id, axisCrvNorm, keeper.name);
          alive.Add(keeper.id);
          used.Add(keepIdx);

          // 删除 component 内其它轴线（只删这一链条的，不动别的）
          foreach (int idx in comp)
          {
            used.Add(idx);
            if (idx == keepIdx) continue;

            var delId = list[idx].id;
            if (doc.Objects.FindId(delId) != null)
            {
              doc.Objects.Delete(delId, true);
              removedSet.Add(delId);
            }
          }
        }
      }

      // 未 join 成功的：只 Normalize，不删
      for (int i = 0; i < list.Count; i++)
      {
        if (used.Contains(i)) continue;

        Curve axisCrvNorm;
        GetAxisKindAndNormalize(segs[i], tol, out axisCrvNorm);

        ReplaceAxisCurveKeepName(doc, list[i].id, axisCrvNorm, list[i].name);
        alive.Add(list[i].id);
      }
    }
  }
  finally
  {
    // 恢复锁
    foreach (var kv in lockBackup)
    {
      int li = kv.Key;
      var ly = doc.Layers[li];
      if (ly == null) continue;
      ly.IsLocked = kv.Value;
      doc.Layers.Modify(ly, li, true);
    }
  }

  // 输出 removedByJoin（只保留真的已经不存在的）
  removedByJoin.Clear();
  foreach (var id in removedSet)
    if (doc.Objects.FindId(id) == null)
      removedByJoin.Add(id);

  // 回写 axisIds（让 Refresh/Redraw 后续逻辑只用“仍存在”的轴线）
  axisIds.Clear();
  foreach (var id in alive.OrderBy(x => x))
    if (doc.Objects.FindId(id) != null)
      axisIds.Add(id);
}




// 只按 “wallcap + axisid=GUID” 严格删除；
// deleteOrphanCaps=true 时：额外删除 “wallcap + axisid=GUID，但该GUID在文档里已不存在”的孤儿cap（仍然不做距离兜底）。
int DeleteWallCapsByAxisIds(
  RhinoDoc doc,
  IEnumerable<Guid> axisIds,
  bool deleteOrphanCaps = false)
{
  if (doc == null) return 0;
  if (axisIds == null) return 0;

  var axisSet = new HashSet<Guid>();
  foreach (var id in axisIds)
    if (id != Guid.Empty) axisSet.Add(id);

  if (axisSet.Count == 0) return 0;

  // ★ 当前工作父级（父级为空则用当前层自身当父级根）
    Guid parentId = Guid.Empty;
    int curLi = doc.Layers.CurrentLayerIndex;
    if (curLi >= 0 && curLi < doc.Layers.Count)
 {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
   {
    parentId = curLay.ParentLayerId;
    if (parentId == Guid.Empty) parentId = curLay.Id;
   }
 }


  bool TryGetAxisIdFromName(string name, out Guid axisId)
  {
    axisId = Guid.Empty;
    if (string.IsNullOrEmpty(name)) return false;

    var m = Regex.Match(
      name,
      @"(?i)\baxisid\b\s*[:=\|]\s*([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");
    if (m.Success && Guid.TryParse(m.Groups[1].Value, out axisId))
      return true;

    // 兜底：仅当只有1个GUID时才认（避免名字里多GUID误判）
    var ms = Regex.Matches(
      name,
      @"(?i)\b([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");
    if (ms != null && ms.Count == 1 && Guid.TryParse(ms[0].Groups[1].Value, out axisId))
      return true;

    return false;
  }

  void TrySetEnum(ObjectEnumeratorSettings s, string prop, bool v)
  {
    var pi = s.GetType().GetProperty(prop);
    if (pi != null && pi.CanWrite && pi.PropertyType == typeof(bool))
      pi.SetValue(s, v, null);
  }

  // 枚举设置：尽量把锁层/隐藏也枚举出来（用反射避免你之前的“属性不存在”编译报错）
  var oes = new ObjectEnumeratorSettings
  {
    ObjectTypeFilter = ObjectType.Curve,
    IncludeLights    = false,
    IncludeGrips     = false
  };
  TrySetEnum(oes, "ActiveObjects",    true);
  TrySetEnum(oes, "NormalObjects",    true);
  TrySetEnum(oes, "LockedObjects",    true);
  TrySetEnum(oes, "HiddenObjects",    true);
  TrySetEnum(oes, "ReferenceObjects", true);

  bool ShouldDeleteCap(RhinoObject ro)
  {
    if (ro == null) return false;
    if (!(ro.Geometry is Curve)) return false;

    string nm  = ro.Attributes.Name ?? "";
    string nml = nm.ToLowerInvariant();

    if (!nml.Contains("wallcap")) return false;

    Guid aid;
    if (!TryGetAxisIdFromName(nm, out aid))
      return false; // 严格模式：没axisid就不删（避免误删）

    if (axisSet.Contains(aid))
      return true;

    if (deleteOrphanCaps)
    {
      // 孤儿cap：axisid这根轴线在文档里已经不存在了
      if (doc.Objects.FindId(aid) == null)
        return true;
    }

    return false;
  }

  // 先收集，不要边枚举边删
  var toDelete = new List<RhinoObject>();
  var layersToUnlock = new HashSet<int>();

    foreach (var ro in doc.Objects.GetObjectList(oes))
  {
  if (ro == null) continue;

  int li = ro.Attributes.LayerIndex;
  if (li < 0 || li >= doc.Layers.Count) continue;

  // ★ 新增：只处理当前分区（parentId 下）的对象
  if (parentId != Guid.Empty && !IsLayerUnderParent(doc, li, parentId))
    continue;

  // 过了分区过滤，再做 Name 解析（避免对外部分区做 Regex）
  if (!ShouldDeleteCap(ro)) continue;

  toDelete.Add(ro);

  var ly = doc.Layers[li];
  if (ly != null && ly.IsLocked)
    layersToUnlock.Add(li);
  }


  if (toDelete.Count == 0) return 0;

  // 解锁相关图层
  var lockBackup = new Dictionary<int, bool>();
  foreach (int li in layersToUnlock)
  {
    var layer = doc.Layers[li];
    if (layer == null) continue;
    lockBackup[li] = layer.IsLocked;
    if (layer.IsLocked) layer.IsLocked = false;
  }

  int removed = 0;
  foreach (var ro in toDelete)
  {
    // 如果对象本身被锁，也先解锁一下再删（更稳）
    if (ro != null && ro.IsLocked)
    {
  var a = ro.Attributes.Duplicate();
  a.Mode = ObjectMode.Normal;
  doc.Objects.ModifyAttributes(ro, a, true);
    }

    if (doc.Objects.Delete(ro, true))
      removed++;
  }

  // 还原图层锁
  foreach (var kv in lockBackup)
  {
    var layer = doc.Layers[kv.Key];
    if (layer != null) layer.IsLocked = kv.Value;
  }

  return removed;
}

// 只按 “WallHatch|AxisId=GUID” 严格删除；
// deleteOrphanHatches=true 时：额外删除 “WallHatch|AxisId=GUID，但该GUID在文档里已不存在”的孤儿 hatch。
int DeleteWallHatchesByAxisIds(
  RhinoDoc doc,
  IEnumerable<Guid> axisIds,
  bool deleteOrphanHatches = false)
{
  if (doc == null) return 0;
  if (axisIds == null) return 0;

  var axisSet = new HashSet<Guid>();
  foreach (var id in axisIds)
    if (id != Guid.Empty) axisSet.Add(id);

  // ★ 当前工作父级（父级为空则用当前层自身当父级根）
  Guid parentId = Guid.Empty;
  int curLi = doc.Layers.CurrentLayerIndex;
  if (curLi >= 0 && curLi < doc.Layers.Count)
  {
    var curLay = doc.Layers[curLi];
    if (curLay != null)
    {
      parentId = curLay.ParentLayerId;
      if (parentId == Guid.Empty) parentId = curLay.Id;
    }
  }

  // 若既没有指定 axisSet，且也不需要删孤儿，则直接返回
  if (axisSet.Count == 0 && !deleteOrphanHatches) return 0;

  int removed = 0;

  // 记录并临时解锁图层
  var lockBackup = new Dictionary<int, bool>();
  void EnsureLayerUnlocked(int layerIndex)
  {
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count) return;
    if (lockBackup.ContainsKey(layerIndex)) return;

    var layer = doc.Layers[layerIndex];
    if (layer == null) return;

    lockBackup[layerIndex] = layer.IsLocked;
    if (layer.IsLocked)
    {
      layer.IsLocked = false;
      doc.Layers.Modify(layer, layerIndex, true);
    }
  }

  try
  {
    var allHatches = doc.Objects.FindByObjectType(ObjectType.Hatch);
    if (allHatches == null || allHatches.Length == 0) return 0;

    foreach (var hro in allHatches)
    {
      if (hro == null) continue;

      int li = hro.Attributes.LayerIndex;
      if (li < 0 || li >= doc.Layers.Count) continue;
      if (parentId != Guid.Empty && !IsLayerUnderParent(doc, li, parentId)) continue;

      string name = hro.Attributes?.Name ?? "";
      if (!name.StartsWith("WallHatch|", StringComparison.OrdinalIgnoreCase))
        continue;

      Guid axisId;
      if (!TryParseAxisIdFromName(name, out axisId))
        continue;

      bool shouldDelete = axisSet.Contains(axisId);

      if (!shouldDelete && deleteOrphanHatches)
      {
        // 轴线已不存在 → 孤儿 hatch
        if (doc.Objects.FindId(axisId) == null)
          shouldDelete = true;
      }

      if (!shouldDelete) continue;

      EnsureLayerUnlocked(li);
      if (doc.Objects.Delete(hro, true))
        removed++;
    }

    return removed;
  }
  finally
  {
    // 恢复图层锁
    foreach (var kv in lockBackup)
    {
      int li = kv.Key;
      var ly = doc.Layers[li];
      if (ly == null) continue;
      ly.IsLocked = kv.Value;
      doc.Layers.Modify(ly, li, true);
    }

    // 保险：把 Hatch 图层锁回去（你已有这套约束的话）
    try { ForceLockWallHatchLayer(doc, parentId); } catch { }
  }
}


//墙体封口函数
void ApplyWallCapsForAxesToBeDeal(RhinoDoc doc)
{
  if (doc == null) return;

  var axisIds = RTZ3State.axistobedeal;
  if (axisIds == null || axisIds.Count == 0)
  {
    RhinoApp.WriteLine("[Cap] axistobedeal 为空，跳过封口。");
    return;
  }

  double tol = doc.ModelAbsoluteTolerance;

  // ===== 参数 =====
  const double SearchRadius = 500.0;

  // 端点“同点判定”的量化容差：比模型 tol 放大一些
  double keySize = Math.Max(tol * 100.0, 0.1);
  double keySize2 = keySize * keySize;

  // ---------- 0) 收集 axistobedeal 里的轴线对象 ----------
  var axisCurveMap = new Dictionary<Guid, Curve>();
  var axisObjMap   = new Dictionary<Guid, RhinoObject>();

  foreach (var id in axisIds)
  {
    var ro = doc.Objects.FindId(id);
    if (ro == null) continue;

    var crv = ro.Geometry as Curve;
    if (crv == null || !crv.IsValid || crv.IsShort(tol)) continue;

    axisObjMap[id]   = ro;
    axisCurveMap[id] = crv;
  }

  if (axisCurveMap.Count == 0)
  {
    RhinoApp.WriteLine("[Cap] axistobedeal 里没有有效轴线对象。");
    return;
  }

  // ---------- 0.1) 反推“本分区 parentId”（严格限定在同一父级图层树内） ----------
  Guid parentId = Guid.Empty;
  foreach (var kv in axisObjMap)
  {
    int li = kv.Value.Attributes.LayerIndex;
    var layer = (li >= 0) ? doc.Layers[li] : null;
    if (layer != null)
    {
      // 轴线一般在 “PB-Wall-Axis” 子层上，它的 ParentLayerId 就是分区 parent
      parentId = layer.ParentLayerId;
      break;
    }
  }

  bool IsLayerUnderParent(int layerIndex, Guid pid)
  {
    if (pid == Guid.Empty) return true; // 没有 parent 概念就不限制
    if (layerIndex < 0) return false;

    var lay = doc.Layers[layerIndex];
    while (lay != null)
    {
      if (lay.Id == pid) return true;
      if (lay.ParentLayerId == Guid.Empty) break;
      lay = doc.Layers.FindId(lay.ParentLayerId);
    }
    return false;
  }

  // ---------- 1) 构建“同分区所有 Wallaxis 端点”的空间索引（RTree） ----------
  var settings = new ObjectEnumeratorSettings
  {
    ObjectTypeFilter = ObjectType.Curve,
    IncludeLights    = false,
    IncludeGrips     = false,
    ActiveObjects    = true,
    NormalObjects    = true,
    LockedObjects    = true,
    HiddenObjects    = true,
    ReferenceObjects = true
  };

  var rtree = new RTree();
  var endAxisIds = new List<Guid>();
  var endPts     = new List<Point3d>();
  var axisWallKindById = new Dictionary<Guid, WallKind>();


  void AddEnd(Guid aid, Point3d p)
  {
    int idx = endPts.Count;
    endAxisIds.Add(aid);
    endPts.Add(p);
    rtree.Insert(p, idx);
  }

  int allAxisCount = 0;

  foreach (var ro in doc.Objects.GetObjectList(settings))
  {
    if (ro == null) continue;
    if (!(ro.Geometry is Curve)) continue;

    // 只认轴线：Name 以 Wallaxis 开头（和你体系一致）
    string nm = ro.Attributes.Name ?? "";
    string wallTag = GetTagString(nm, "walltype", "block");

    if (!nm.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
      continue;

    // 严格限制在同分区
    if (!IsLayerUnderParent(ro.Attributes.LayerIndex, parentId))
      continue;

    var crv = ro.Geometry as Curve;
    if (crv == null || !crv.IsValid || crv.IsShort(tol)) continue;

    AddEnd(ro.Id, crv.PointAtStart);
    AddEnd(ro.Id, crv.PointAtEnd);
        
    axisWallKindById[ro.Id] = ParseWallKindTag(wallTag);

    allAxisCount++;
  }

  if (endPts.Count == 0)
  {
    RhinoApp.WriteLine("[Cap] 未扫描到本分区任何 Wallaxis，跳过封口。");
    return;
  }

  // 对某个端点：在半径 500 内找“是否存在其它轴线端点与它同点”
  bool IsOpenEnd(Guid selfAxisId, Point3d p)
  {
    bool hasOtherSamePoint = false;

    var sphere = new Sphere(p, SearchRadius);
    rtree.Search(sphere, (sender, args) =>
    {
      if (hasOtherSamePoint) return;

      int idx = args.Id;
      if (idx < 0 || idx >= endPts.Count) return;

      var otherAxisId = endAxisIds[idx];
      if (otherAxisId == selfAxisId) return; // 排除自身（不算连接）

      if (endPts[idx].DistanceToSquared(p) <= keySize2)
        hasOtherSamePoint = true;
    });

    return !hasOtherSamePoint;
  }

  WallKind GetAxisWallKind(Guid id)
 {
  if (axisWallKindById.TryGetValue(id, out var k)) return k;
  return WallKind.Block;
 }

  // 8.2：原判定（孤端点）保持不变；新增：同一点多根轴线汇交，且 walltype 两两不同 -> 最高等级那根封口
  bool ShouldCapEnd(Guid axisId, Point3d endPt)
 {
  // 1) 原规则：这个端点附近没有其它任何轴线端点
  if (IsOpenEnd(axisId, endPt))
    return true;

  // 2) 新规则：该节点上有多根轴线端点重合（严格限制在本父级图层内：你这套 rtree/endAxisIds 本来就只收了本 parent）
  var nodeAxisIds = new List<Guid>();

    rtree.Search(new Sphere(endPt, SearchRadius), (s, a) =>
 {
  int idx = a.Id;
  if (idx < 0 || idx >= endPts.Count) return;

  // ★ 用 endPts[idx] 取点；并用 DistanceToSquared 对应 keySize2
  if (endPts[idx].DistanceToSquared(endPt) <= keySize2)
    nodeAxisIds.Add(endAxisIds[idx]);
 });


  // 去重
  var uniq = new List<Guid>();
  var seen = new HashSet<Guid>();
  foreach (var id in nodeAxisIds)
    if (seen.Add(id)) uniq.Add(id);

  if (uniq.Count <= 1) return false;

  // 3) walltype 必须两两不同；并找出最高等级那根
  var usedKinds = new HashSet<WallKind>();
  Guid bestAxis = Guid.Empty;
  int bestLevel = int.MinValue;

  foreach (var id in uniq)
  {
    var k = GetAxisWallKind(id);

    // 只要出现重复 walltype，本规则不触发（维持你原逻辑）
    if (!usedKinds.Add(k))
      return false;

    int lv = GetWallKindLevel(k);
    if (lv > bestLevel)
    {
      bestLevel = lv;
      bestAxis = id;
    }
  }

  return bestAxis == axisId;
 }

  

  // ---------- 2) 扫描墙线：不依赖图层名，只靠 Name 里的 axisid ----------
  bool TryGetAxisIdFromName(string name, out Guid axisId)
  {
    axisId = Guid.Empty;
    if (string.IsNullOrEmpty(name)) return false;

    var m = Regex.Match(
      name,
      @"(?i)\baxisid\b\s*[:=\|]\s*([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");
    if (m.Success && Guid.TryParse(m.Groups[1].Value, out axisId))
      return true;

    var ms = Regex.Matches(
      name,
      @"(?i)\b([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");
    if (ms != null && ms.Count == 1 && Guid.TryParse(ms[0].Groups[1].Value, out axisId))
      return true;

    return false;
  }

  bool TryGetSideFromName(string name, out bool isLeft, out bool isRight)
  {
    isLeft = false; isRight = false;
    if (string.IsNullOrEmpty(name)) return false;

    string n = name.ToLowerInvariant();

    var m = Regex.Match(n, @"\bside\s*=\s*(left|right|l|r)\b");
    if (m.Success)
    {
      string v = m.Groups[1].Value;
      if (v == "left" || v == "l") isLeft = true;
      if (v == "right" || v == "r") isRight = true;
      return isLeft || isRight;
    }

    if (n.Contains("wallleft"))  { isLeft = true;  return true; }
    if (n.Contains("wallright")) { isRight = true; return true; }

    if (n.Contains("|left|"))  { isLeft = true;  return true; }
    if (n.Contains("|right|")) { isRight = true; return true; }

    return false;
  }

  var wallByAxis = new Dictionary<Guid, List<RhinoObject>>();

  foreach (var ro in doc.Objects.GetObjectList(settings))
  {
    if (ro == null) continue;
    if (!(ro.Geometry is Curve)) continue;

    // 严格限制在同分区
    if (!IsLayerUnderParent(ro.Attributes.LayerIndex, parentId))
      continue;

    string nm  = ro.Attributes.Name ?? "";
    string nml = nm.ToLowerInvariant();

    // 排除：轴线 / 保温 / 封口
    if (nml.StartsWith("wallaxis")) continue;
    if (nml.Contains("wallcap"))    continue;
    if (nml.Contains("insul"))      continue;

    Guid aid;
    if (!TryGetAxisIdFromName(nm, out aid)) continue;
    if (!axisCurveMap.ContainsKey(aid)) continue;

    if (!wallByAxis.TryGetValue(aid, out var list))
    {
      list = new List<RhinoObject>();
      wallByAxis[aid] = list;
    }
    list.Add(ro);
  }

  // ---------- 2.9) 临时解锁相关图层（墙线层 + 轴线层），保证可落图/可改名 ----------
  var layersToUnlock = new HashSet<int>();
  var lockBackup = new Dictionary<int, bool>();

  foreach (var kv in axisObjMap)
  {
    int li = kv.Value.Attributes.LayerIndex;
    if (li >= 0 && doc.Layers[li] != null && doc.Layers[li].IsLocked) layersToUnlock.Add(li);
  }

  foreach (var kv in wallByAxis)
  {
    foreach (var ro in kv.Value)
    {
      int li = ro.Attributes.LayerIndex;
      if (li >= 0 && doc.Layers[li] != null && doc.Layers[li].IsLocked) layersToUnlock.Add(li);
    }
  }

  foreach (int li in layersToUnlock)
  {
    var layer = doc.Layers[li];
    if (layer == null) continue;
    lockBackup[li] = layer.IsLocked;
    if (layer.IsLocked) layer.IsLocked = false;
  }

  // ---------- 3) 生成新封口线 + 更新轴线 cap 标签 ----------
  string SetOrReplaceTag(string name, string keyLower, int v01)
  {
    if (string.IsNullOrEmpty(name)) name = "Wallaxis";
    string key = keyLower.ToLowerInvariant();
    string pattern = @"(?i)(\|" + Regex.Escape(key) + @"\s*=\s*[01])";
    string replacement = "|" + key + "=" + v01;
    if (Regex.IsMatch(name, pattern))
      return Regex.Replace(name, pattern, replacement);
    else
      return name + replacement;
  }

  
  // ★ 1.1.2.2：封口端点优先选“外墙线 Wall|”，不要被玻璃幕墙内墙线 Wallinside| 覆盖
  int GetWallCapPreferRank(string name)
  {
    if (string.IsNullOrEmpty(name)) return 0;
    string n = name.Trim();
    if (n.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase)) return 3;        // 外墙线
    if (n.StartsWith("Wallinside|", StringComparison.OrdinalIgnoreCase)) return 2;  // 内墙线（兜底）
    // 兼容旧命名（极少用到）：含 wallleft/right 的也给一点权重
    string nl = n.ToLowerInvariant();
    if (nl.Contains("wallleft") || nl.Contains("wallright")) return 1;
    return 0;
  }

bool TryPickLeftRightWalls(Guid axisId, out RhinoObject wallLObj, out RhinoObject wallRObj)
  {
    wallLObj = null; wallRObj = null;
    if (!wallByAxis.TryGetValue(axisId, out var list) || list == null || list.Count == 0)
      return false;

    foreach (var ro in list)
    {
      bool isL, isR;
      if (!TryGetSideFromName(ro.Attributes.Name ?? "", out isL, out isR))
        continue;

      int rNew = GetWallCapPreferRank(ro.Attributes.Name ?? "");

      if (isL)
      {
        if (wallLObj == null) wallLObj = ro;
        else
        {
          int rOld = GetWallCapPreferRank(wallLObj.Attributes.Name ?? "");
          if (rNew > rOld) wallLObj = ro;
        }
      }

      if (isR)
      {
        if (wallRObj == null) wallRObj = ro;
        else
        {
          int rOld = GetWallCapPreferRank(wallRObj.Attributes.Name ?? "");
          if (rNew > rOld) wallRObj = ro;
        }
      }
    }

    if ((wallLObj == null || wallRObj == null) && list.Count == 2)
    {
      wallLObj ??= list[0];
      wallRObj ??= list[1];
    }

    return wallLObj != null && wallRObj != null;
  }

  int madeCaps = 0;

  foreach (var axisId in axisCurveMap.Keys)
  {
    var axisCrv = axisCurveMap[axisId];
    if (axisCrv == null || !axisCrv.IsValid) continue;

    // ★ 新逻辑：以端点为基础，在半径 500 内找“是否存在其它轴线端点同点”
    bool openStart = ShouldCapEnd(axisId, axisCrv.PointAtStart);
    bool openEnd   = ShouldCapEnd(axisId, axisCrv.PointAtEnd);


    int capStOn = 0, capEdOn = 0;
    if (TryPickLeftRightWalls(axisId, out var wallLObj, out var wallRObj))
    {
      var wallLCrv = wallLObj.Geometry as Curve;
      var wallRCrv = wallRObj.Geometry as Curve;

      if (wallLCrv != null && wallRCrv != null && wallLCrv.IsValid && wallRCrv.IsValid)
      {
        var baseAttr = wallLObj.Attributes.Duplicate();
        int wallLayerIndex = wallLObj.Attributes.LayerIndex;

        if (openStart)
        {
          // ★ 1.3.2：封口端点严格对应轴线端点：轴线 Start → 墙线 Start
          var pL = wallLCrv.PointAtStart;
          var pR = wallRCrv.PointAtStart;

          var attr = baseAttr.Duplicate();
          attr.LayerIndex = wallLayerIndex;
          attr.Name = "wallcap|axisid=" + axisId + "|at=st";
          doc.Objects.AddCurve(new LineCurve(pL, pR), attr);
          capStOn = 1; madeCaps++;
        }

        if (openEnd)
        {
          // ★ 1.3.2：封口端点严格对应轴线端点：轴线 End → 墙线 End
          var pL = wallLCrv.PointAtEnd;
          var pR = wallRCrv.PointAtEnd;

          var attr = baseAttr.Duplicate();
          attr.LayerIndex = wallLayerIndex;
          attr.Name = "wallcap|axisid=" + axisId + "|at=ed";
          doc.Objects.AddCurve(new LineCurve(pL, pR), attr);
          capEdOn = 1; madeCaps++;
        }
      }
    }

// 更新轴线 cap 标签（无论是否生成 cap，都写 0/1）
    if (axisObjMap.TryGetValue(axisId, out var axisObj))
    {
      var a = axisObj.Attributes.Duplicate();
      string nm = a.Name ?? "";
      nm = SetOrReplaceTag(nm, "capston", capStOn);
      nm = SetOrReplaceTag(nm, "capedon", capEdOn);
      a.Name = nm;
      doc.Objects.ModifyAttributes(axisObj, a, true);
    }
  }

  // 还原锁
  foreach (var kv in lockBackup)
  {
    var layer = doc.Layers[kv.Key];
    if (layer != null) layer.IsLocked = kv.Value;
  }

  doc.Views.Redraw();
}

// ==================== 11. 钢筋混凝土墙填充（Hatch） ====================
//
// 说明：
// - 仅对 Walltype=Concrete 的轴线生成实体填充（Solid Hatch）
// - Hatch 放到当前 parentId 下的 PB-Wall-Hatch 图层（灰 128,128,128；线宽 0.1；默认锁定，并在每次操作结束强制锁回）
// - Hatch Name：WallHatch|AxisId=xxxx（用于删除旧填充）
// - 生成 Hatch 的临时封口/轮廓线都不写入文档（只在内存中拼闭合轮廓）
//
// “半封口/全封口” 判定：
// - 对某个轴线端点（或交点）处：若该点处钢筋混凝土墙轴线数量 >= 2 → 全封口；否则 → 半封口
// - 半封口：墙线端点直接连线
// - 全封口：墙线端点分别与轴线端点连线（两段线）
// - 这里不做最近点匹配：墙轴线 Start/End 与墙线 Start/End 默认同侧，直接用 Start/End 对应即可
//
// ============================================================

int EnsureWallHatchLayerIndex(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return -1;

  int li = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Hatch");
  if (li >= 0 && li < doc.Layers.Count)
  {
    var lay = doc.Layers[li];
    if (lay != null)
    {
      // 若 Hatch 图层已存在：
      // ✅ 不再强行改 SDColor / PlotColor / PlotWeight（尊重用户后续手动修改）
      // 仍允许按既有约束强制锁回。
      if (!lay.IsLocked)
      {
        lay.IsLocked = true;
        doc.Layers.Modify(lay, li, true);
      }
    }
    return li;
  }

  // 新建
  var hatchLayer = new Layer();
  hatchLayer.ParentLayerId = parentId;
  hatchLayer.Name          = "PB-Wall-Hatch";
  hatchLayer.Color         = SDColor.FromArgb(140, 140, 140);
  hatchLayer.PlotColor     = SDColor.FromArgb(140, 140, 140);
  hatchLayer.PlotWeight    = 0.1;
  hatchLayer.IsLocked      = true;

  li = doc.Layers.Add(hatchLayer);
  return li;
}

void ForceLockWallHatchLayer(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return;
  int li = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Hatch");
  if (li < 0 || li >= doc.Layers.Count) return;

  var lay = doc.Layers[li];
  if (lay == null) return;

  if (!lay.IsLocked)
  {
    lay.IsLocked = true;
    doc.Layers.Modify(lay, li, true);
  }
}


// ---------- 14) 墙线大兜底：孤立端头延长（1500mm，先碰停） ----------
void ApplyWallBigFallbackForOrphanEnds(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return;

  double tol = doc.ModelAbsoluteTolerance;
  if (tol <= 0) tol = 1e-6;

  // 单位：按 mm 设定 1500
  double mm = 1.0;
  try
  {
    mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
    if (mm <= 0) mm = 1.0;
  }
  catch { mm = 1.0; }

  double maxExt = 1500.0 * mm;
  if (maxExt <= tol) return;

  double connectTol = Math.Max(tol * 2.0, 1.0 * mm);
  double grid = Math.Max(connectTol * 4.0, 4.0 * mm);

  double preCut = 150.0 * mm; // 14.x 补充：孤立端头附近 150mm 内若已与墙线相交，则先截断不再延长

  // 1.2.6：仅处理“本轮轴线(axistobedeal)”对应的墙线，避免重绘/编辑时误修复未选中的远处墙
  HashSet<Guid> scopeAxisIds = null;
  try
  {
    if (RTZ3State.axistobedeal != null && RTZ3State.axistobedeal.Count > 0)
      scopeAxisIds = new HashSet<Guid>(RTZ3State.axistobedeal);
  }
  catch { scopeAxisIds = null; }


  // 只处理本父级下的墙线层（不含轴线层/封口线以外对象），封口线仅作“碰停障碍”
  var wallLayerList = new List<int>();
  int liWall  = (parentId == Guid.Empty) ? doc.Layers.FindByFullPath("PB-Wall", -1) : FindSiblingLayerIndex(doc, parentId, "PB-Wall");
  int liGlas  = (parentId == Guid.Empty) ? doc.Layers.FindByFullPath("PB-Wall-Glas", -1) : FindSiblingLayerIndex(doc, parentId, "PB-Wall-Glas");
  int liStud  = (parentId == Guid.Empty) ? doc.Layers.FindByFullPath("PB-Wall-Stud", -1) : FindSiblingLayerIndex(doc, parentId, "PB-Wall-Stud");

  if (liWall >= 0) wallLayerList.Add(liWall);
  if (liGlas >= 0 && liGlas != liWall) wallLayerList.Add(liGlas);
  if (liStud >= 0 && liStud != liWall && liStud != liGlas) wallLayerList.Add(liStud);

  if (wallLayerList.Count == 0) return;

  // 2) 解锁墙线层（避免 Replace/ModifyAttributes 失败）
  var lockBackup = new Dictionary<int, bool>();
  foreach (int li in wallLayerList)
  {
    if (li < 0 || li >= doc.Layers.Count) continue;
    var layer = doc.Layers[li];
    if (layer == null) continue;
    lockBackup[li] = layer.IsLocked;
    if (layer.IsLocked)
      layer.IsLocked = false;
  }

  // enum curves in a layer (snapshot)
  IEnumerable<RhinoObject> EnumLayerCurvesLocal(int li)
  {
    if (li < 0 || li >= doc.Layers.Count) yield break;
    var ly = doc.Layers[li];
    if (ly == null) yield break;

    var objs = doc.Objects.FindByLayer(ly); // 数组快照
    if (objs == null) yield break;

    foreach (var oo in objs)
      if (oo != null && oo.ObjectType == ObjectType.Curve)
        yield return oo;
  }

  // 3) 收集：墙线（处理对象） + 封口线（障碍）= obs
  var obsCurves = new Dictionary<Guid, Curve>();
  var ends = new Dictionary<Guid, (Point3d p0, Point3d p1)>();
  var wallIds = new List<Guid>();

  var allWallIdsAll = new HashSet<Guid>(); // 本父级下所有墙线（Wall/Wallinside），用于 150mm 截断判定

  foreach (int li in wallLayerList)
  {
    foreach (var obj in EnumLayerCurvesLocal(li))
    {
      var ro = obj as RhinoObject;
      if (ro == null) continue;

      string name = (ro.Attributes.Name ?? "").Trim();
      if (string.IsNullOrWhiteSpace(name)) continue;

      bool isWall = name.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("Wallinside|", StringComparison.OrdinalIgnoreCase);
      bool isCap  = name.StartsWith("wallcap|", StringComparison.OrdinalIgnoreCase);

      if (!isWall && !isCap) continue;

      var crv = ro.Geometry as Curve;
      if (crv == null || !crv.IsValid || crv.IsShort(tol)) continue;

      Guid id = ro.Id;
      if (id == Guid.Empty) continue;

      Curve dup = null;
      try { dup = crv.DuplicateCurve(); } catch { dup = null; }
      if (dup == null || !dup.IsValid) continue;

      obsCurves[id] = dup;
      var p0 = dup.PointAtStart;
      var p1 = dup.PointAtEnd;
      ends[id] = (p0, p1);

      if (isWall && !isCap)
      {
        allWallIdsAll.Add(id);
        if (scopeAxisIds == null)
        {
          wallIds.Add(id);
        }
        else
        {
          Guid axisIdInWall;
          if (TryParseAxisIdFromName(name, out axisIdInWall) && axisIdInWall != Guid.Empty && scopeAxisIds.Contains(axisIdInWall))
            wallIds.Add(id);
        }
      }
    }
  }

  if (obsCurves.Count == 0 || wallIds.Count == 0)
  {
    // restore layer lock
    foreach (var kv in lockBackup)
    {
      int li = kv.Key;
      if (li >= 0 && li < doc.Layers.Count)
      {
        var layer = doc.Layers[li];
        if (layer != null) layer.IsLocked = kv.Value;
      }
    }
    return;
  }

  // 4) 端点索引（墙线+封口线）
  var endMap = new Dictionary<PtKey, HashSet<Guid>>();
  foreach (var kv in ends)
  {
    Guid id = kv.Key;
    var (p0, p1) = kv.Value;
    IndexEndpoint(endMap, p0, id, grid);
    IndexEndpoint(endMap, p1, id, grid);
  }

  // 5) 曲线 RTree（墙线+封口线，用于点落线判定与延长“碰停”）
  var curveList = new List<(Guid id, Curve crv, BoundingBox bb)>(obsCurves.Count);
  var rtree = new RTree();

  int rid = 0;
  foreach (var kv in obsCurves)
  {
    var c = kv.Value;
    BoundingBox bb;
    try { bb = c.GetBoundingBox(true); } catch { bb = BoundingBox.Empty; }
    if (!bb.IsValid) bb = new BoundingBox(c.PointAtStart, c.PointAtEnd);
    bb.Inflate(connectTol);
    curveList.Add((kv.Key, c, bb));
    try { rtree.Insert(bb, rid); } catch { }
    rid++;
  }

  bool IsOrphanEnd(Guid selfId, Point3d pt)
  {
    // 5.1) 端点附近是否有“其他端点”
    foreach (var otherId in QueryNearEndpoint(endMap, pt, grid))
    {
      if (otherId == Guid.Empty || otherId == selfId) continue;
      if (!ends.TryGetValue(otherId, out var ep)) continue;

      if (pt.DistanceTo(ep.p0) <= connectTol) return false;
      if (pt.DistanceTo(ep.p1) <= connectTol) return false;
    }

    // 5.2) 是否“落在任何墙线/封口线”上（排除自身）
    bool onOther = false;
    var sph = new Sphere(pt, connectTol);
    try
    {
      rtree.Search(sph, (sender, args) =>
      {
        if (args == null) return;
        int ii = args.Id;
        if (ii < 0 || ii >= curveList.Count) return;

        var it = curveList[ii];
        if (it.id == selfId) return;

        double t;
        if (!it.crv.ClosestPoint(pt, out t)) return;

        var cp = it.crv.PointAt(t);
        if (cp.DistanceTo(pt) <= connectTol)
        {
          onOther = true;
}
      });
    }
    catch { }

    if (onOther) return false;
    return true;
  }

  CurveExtensionStyle PickExtStyle(Curve c)
  {
    if (c == null) return CurveExtensionStyle.Smooth;

    

    try { if (c.IsLinear(tol)) return CurveExtensionStyle.Line; } catch { }
try
    {
      // 线：直线延长；圆弧/圆：圆弧延长；其他：平滑延长
      var lc = c as LineCurve;
      if (lc != null) return CurveExtensionStyle.Line;

      Arc a;
      if (c.TryGetArc(out a)) return CurveExtensionStyle.Arc;

      Circle cc;
      if (c.TryGetCircle(out cc)) return CurveExtensionStyle.Arc;
    }
    catch { }

    return CurveExtensionStyle.Smooth;
  }


// 14.x 补充：如果孤立端头附近 150mm 内，墙线已与“某墙线”相交，则先被该墙线截断（去掉孤立小尾巴），不再延长
bool TryTrimOrphanTailByNearbyWallIntersection(Guid selfId, Curve src, Rhino.Geometry.CurveEnd orphanEnd, out Curve finalCrv)
{
  finalCrv = null;
  if (src == null || !src.IsValid) return false;
  if (preCut <= connectTol) return false;
  if (allWallIdsAll == null || allWallIdsAll.Count == 0) return false;

  Point3d P = (orphanEnd == Rhino.Geometry.CurveEnd.Start) ? src.PointAtStart : src.PointAtEnd;

  // 搜索候选：以孤立端头为中心的球
  var sph = new Sphere(P, preCut + connectTol);

  var candIdx = new List<int>();
  try
  {
    rtree.Search(sph, (sender, args) =>
    {
      if (args == null) return;
      candIdx.Add(args.Id);
    });
  }
  catch { }

  double bestD = double.MaxValue;
  double bestT = double.NaN;

  foreach (int ii in candIdx)
  {
    if (ii < 0 || ii >= curveList.Count) continue;
    var it = curveList[ii];
    if (it.id == selfId) continue;

    // 仅“墙线”作为截断对象（不含 wallcap）
    if (!allWallIdsAll.Contains(it.id)) continue;

    if (it.crv == null || !it.crv.IsValid) continue;

    try
    {
      var x = Rhino.Geometry.Intersect.Intersection.CurveCurve(src, it.crv, connectTol, connectTol);
      if (x == null || x.Count == 0) continue;

      foreach (var ev in x)
      {
        if (ev == null) continue;

        if (ev.IsPoint)
        {
          Point3d pt = ev.PointA;
          double d = pt.DistanceTo(P);
          if (d <= connectTol) continue;
          if (d > preCut + connectTol) continue;

          double ta = ev.ParameterA;
          if (d < bestD)
          {
            bestD = d;
            bestT = ta;
          }
        }
        else if (ev.IsOverlap)
        {
          var ov = ev.OverlapA;
          if (!ov.IsValid) continue;

          double ta = (orphanEnd == Rhino.Geometry.CurveEnd.Start) ? ov.T0 : ov.T1;
          Point3d pt = src.PointAt(ta);
          double d = pt.DistanceTo(P);
          if (d <= connectTol) continue;
          if (d > preCut + connectTol) continue;

          if (d < bestD)
          {
            bestD = d;
            bestT = ta;
          }
        }
      }
    }
    catch { }
  }

  if (double.IsNaN(bestT) || double.IsInfinity(bestT)) return false;
  if (bestD > preCut + connectTol) return false;

  try
  {
    Curve tCrv = null;
    if (orphanEnd == Rhino.Geometry.CurveEnd.Start)
      tCrv = src.Trim(bestT, src.Domain.T1);
    else
      tCrv = src.Trim(src.Domain.T0, bestT);

    if (tCrv == null || !tCrv.IsValid || tCrv.IsShort(tol)) return false;

    finalCrv = tCrv;
    return true;
  }
  catch
  {
    finalCrv = null;
    return false;
  }
}


  bool TryBuildExtendedWall(Guid selfId, Curve src, Rhino.Geometry.CurveEnd endToExtend, out Curve finalCrv)
  {
    finalCrv = null;
    if (src == null || !src.IsValid) return false;

    // 仅延长该端，不做反向延长
    Curve ext = null;
    try
    {
      ext = src.DuplicateCurve();
      if (ext == null || !ext.IsValid) return false;

      var style = PickExtStyle(ext);
      ext = ext.Extend(endToExtend, maxExt, style);
    }
    catch { ext = null; }

    if (ext == null || !ext.IsValid) return false;

    // 原端头参数（在 ext 上取）
    Point3d P = (endToExtend == Rhino.Geometry.CurveEnd.Start) ? src.PointAtStart : src.PointAtEnd;

    double t0;
    if (!ext.ClosestPoint(P, out t0)) return false;

    double eps = 1e-9 * (Math.Abs(t0) + Math.Abs(ext.Domain.T0) + Math.Abs(ext.Domain.T1) + 1.0);

    // 在 ext 上找“最先碰到”的点
    double bestLen = double.MaxValue;
    double bestT = double.NaN;

    BoundingBox bbExt;
    try { bbExt = ext.GetBoundingBox(true); } catch { bbExt = BoundingBox.Empty; }
    if (!bbExt.IsValid) bbExt = new BoundingBox(ext.PointAtStart, ext.PointAtEnd);
    bbExt.Inflate(connectTol);

    var candIdx = new List<int>();
    try
    {
      rtree.Search(bbExt, (sender, args) =>
      {
        if (args == null) return;
        candIdx.Add(args.Id);
      });
    }
    catch { }

    foreach (int ii in candIdx)
    {
      if (ii < 0 || ii >= curveList.Count) continue;

      var it = curveList[ii];
      if (it.id == selfId) continue;

      // 6.1) 曲线-曲线相交（允许一定容差，端点也会被捕获）
      try
      {
        var x = Rhino.Geometry.Intersect.Intersection.CurveCurve(ext, it.crv, connectTol, connectTol);
        if (x != null && x.Count > 0)
        {
          foreach (var ev in x)
          {
            if (ev == null) continue;
            if (!ev.IsPoint) continue;

            double ta = ev.ParameterA;

            if (endToExtend == Rhino.Geometry.CurveEnd.End)
            {
              if (ta <= t0 + eps) continue;
            }
            else
            {
              if (ta >= t0 - eps) continue;
            }

            double len = 0.0;
            try
            {
              if (endToExtend == Rhino.Geometry.CurveEnd.End)
                len = ext.GetLength(new Interval(t0, ta));
              else
                len = ext.GetLength(new Interval(ta, t0));
            }
            catch
            {
              len = ev.PointA.DistanceTo(P);
            }

            if (len <= connectTol) continue;
            if (len < bestLen)
            {
              bestLen = len;
              bestT = ta;
            }
          }
        }
      }
      catch { }

      // 6.2) 端点“碰停”（避免容差下 CurveCurve 没抓到）
      try
      {
        var e0 = it.crv.PointAtStart;
        var e1 = it.crv.PointAtEnd;

        void CheckEndPoint(Point3d ept)
        {
          double te;
          if (!ext.ClosestPoint(ept, out te)) return;

          if (endToExtend == Rhino.Geometry.CurveEnd.End)
          {
            if (te <= t0 + eps) return;
          }
          else
          {
            if (te >= t0 - eps) return;
          }

          var cp = ext.PointAt(te);
          if (cp.DistanceTo(ept) > connectTol) return;

          double len = 0.0;
          try
          {
            if (endToExtend == Rhino.Geometry.CurveEnd.End)
              len = ext.GetLength(new Interval(t0, te));
            else
              len = ext.GetLength(new Interval(te, t0));
          }
          catch
          {
            len = cp.DistanceTo(P);
          }

          if (len <= connectTol) return;
          if (len < bestLen)
          {
            bestLen = len;
            bestT = te;
          }
        }

        CheckEndPoint(e0);
        CheckEndPoint(e1);
      }
      catch { }
    }

    // 未在 1500mm 内碰到任何东西：不做兜底
    if (!!(double.IsNaN(bestLen) || double.IsInfinity(bestLen)) || bestLen > maxExt) return false;
    if (!!(double.IsNaN(bestT) || double.IsInfinity(bestT))) return false;

    // 7) 从 ext 上裁切得到“最终墙线”
    try
    {
      Curve tCrv = null;
      if (endToExtend == Rhino.Geometry.CurveEnd.End)
        tCrv = ext.Trim(ext.Domain.T0, bestT);
      else
        tCrv = ext.Trim(bestT, ext.Domain.T1);

      if (tCrv == null || !tCrv.IsValid || tCrv.IsShort(tol)) return false;

      finalCrv = tCrv;
      return true;
    }
    catch
    {
      finalCrv = null;
      return false;
    }
  }

  int changed = 0;

  // 8) 主循环：找孤立端头并兜底（先 start 后 end；都孤立则两端都尝试）
  foreach (var wid in wallIds)
  {
    var ro = doc.Objects.FindId(wid);
    if (ro == null) continue;

    var g = ro.Geometry as Curve;
    if (g == null || !g.IsValid || g.IsShort(tol)) continue;

    Curve src;
    try { src = g.DuplicateCurve(); } catch { src = null; }
    if (src == null || !src.IsValid) continue;

    var pS = src.PointAtStart;
    var pE = src.PointAtEnd;

    bool orphanS = IsOrphanEnd(wid, pS);
    bool orphanE = IsOrphanEnd(wid, pE);

    if (!orphanS && !orphanE) continue;

    // 8.1) start 端
    if (orphanS)
    {
      Curve finalCrv = null;

      // 14.x 补充：150mm 内已与墙线相交 => 先截断，不再延长
      bool didCut = false;
      try
      {
        Curve cutCrv;
        if (TryTrimOrphanTailByNearbyWallIntersection(wid, src, Rhino.Geometry.CurveEnd.Start, out cutCrv) && cutCrv != null)
        {
          if (doc.Objects.Replace(wid, cutCrv))
          {
            var ro2 = doc.Objects.FindId(wid);
            if (ro2 != null)
            {
              var attr = ro2.Attributes.Duplicate();
              string nm = (attr.Name ?? "").Trim();
              nm = SetOrAppendTag(nm, "Start", FormatPointForName(cutCrv.PointAtStart));
              nm = SetOrAppendTag(nm, "End",   FormatPointForName(cutCrv.PointAtEnd));
              attr.Name = nm;
              doc.Objects.ModifyAttributes(wid, attr, true);
            }
            changed++;
            didCut = true;
          }
        }
      }
      catch { didCut = false; }

      if (!didCut)
      {
        if (TryBuildExtendedWall(wid, src, Rhino.Geometry.CurveEnd.Start, out finalCrv) && finalCrv != null)
        {
        try
        {
          if (doc.Objects.Replace(wid, finalCrv))
          {
            // 更新 Name 中的 Start/End（继承其余信息）
            var ro2 = doc.Objects.FindId(wid);
            if (ro2 != null)
            {
              var attr = ro2.Attributes.Duplicate();
              string nm = (attr.Name ?? "").Trim();
              nm = SetOrAppendTag(nm, "Start", FormatPointForName(finalCrv.PointAtStart));
              nm = SetOrAppendTag(nm, "End",   FormatPointForName(finalCrv.PointAtEnd));
              attr.Name = nm;
              doc.Objects.ModifyAttributes(wid, attr, true);
            }
            changed++;
          }
        }
        catch { }
      }
      }

      // 重新取一次（可能已被 Replace）
      ro = doc.Objects.FindId(wid);
      g = ro != null ? ro.Geometry as Curve : null;
      if (g == null || !g.IsValid || g.IsShort(tol)) continue;
      try { src = g.DuplicateCurve(); } catch { src = null; }
      if (src == null || !src.IsValid) continue;
    }

    // 8.2) end 端
    if (orphanE)
    {
      Curve finalCrv = null;

      // 14.x 补充：150mm 内已与墙线相交 => 先截断，不再延长
      bool didCut = false;
      try
      {
        Curve cutCrv;
        if (TryTrimOrphanTailByNearbyWallIntersection(wid, src, Rhino.Geometry.CurveEnd.End, out cutCrv) && cutCrv != null)
        {
          if (doc.Objects.Replace(wid, cutCrv))
          {
            var ro2 = doc.Objects.FindId(wid);
            if (ro2 != null)
            {
              var attr = ro2.Attributes.Duplicate();
              string nm = (attr.Name ?? "").Trim();
              nm = SetOrAppendTag(nm, "Start", FormatPointForName(cutCrv.PointAtStart));
              nm = SetOrAppendTag(nm, "End",   FormatPointForName(cutCrv.PointAtEnd));
              attr.Name = nm;
              doc.Objects.ModifyAttributes(wid, attr, true);
            }
            changed++;
            didCut = true;
          }
        }
      }
      catch { didCut = false; }

      if (!didCut)
      {
        if (TryBuildExtendedWall(wid, src, Rhino.Geometry.CurveEnd.End, out finalCrv) && finalCrv != null)
        {
        try
        {
          if (doc.Objects.Replace(wid, finalCrv))
          {
            var ro2 = doc.Objects.FindId(wid);
            if (ro2 != null)
            {
              var attr = ro2.Attributes.Duplicate();
              string nm = (attr.Name ?? "").Trim();
              nm = SetOrAppendTag(nm, "Start", FormatPointForName(finalCrv.PointAtStart));
              nm = SetOrAppendTag(nm, "End",   FormatPointForName(finalCrv.PointAtEnd));
              attr.Name = nm;
              doc.Objects.ModifyAttributes(wid, attr, true);
            }
            changed++;
          }
        }
        catch { }
      }
      }
    }
  }

  // 9) 恢复锁定状态
  foreach (var kv in lockBackup)
  {
    int li = kv.Key;
    if (li < 0 || li >= doc.Layers.Count) continue;
    var layer = doc.Layers[li];
    if (layer != null) layer.IsLocked = kv.Value;
  }

  if (changed > 0)
  {
    try { doc.Views.Redraw(); } catch { }
  }
}
// ---------- 14) 墙线大兜底 结束 ----------
void ApplyWallHatchForConcreteAxesToBeDeal(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return;

  var axisIds = RTZ3State.axistobedeal;
  if (axisIds == null || axisIds.Count == 0)
  {
    ForceLockWallHatchLayer(doc, parentId);
    return;
  }

  double tol = doc.ModelAbsoluteTolerance;

  // ★ 端点连接判定用更宽松容差（默认至少 1mm）
  double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  double connectTol = Math.Max(tol, 1.0 * mm);
  double grid = connectTol;

  // Join/Hatch 用容差略放宽
  double joinTol = Math.Max(tol * 5.0, connectTol);

  // ------------------------------------------------------------
  // 0) 本轮需要生成 Hatch 的钢筋混凝土轴线（仅 axistobedeal）
  // ------------------------------------------------------------
  var concreteAxisIds = new List<Guid>();
  foreach (var id in axisIds)
  {
    var ro = doc.Objects.FindId(id);
    if (ro == null) continue;

    var crv = ro.Geometry as Curve;
    if (crv == null) continue;

    string nm = ro.Attributes?.Name ?? "";
    if (!nm.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
      continue;

    string wallTag = GetTagString(nm, "walltype", "concrete");
    WallKind kind = ParseWallKindTagOrDefault(wallTag);
    if (kind != WallKind.Concrete) continue;

    concreteAxisIds.Add(id);
  }

  // 即便本轮没有钢筋混凝土轴线，也要强制锁回 Hatch 图层
  if (concreteAxisIds.Count == 0)
  {
    ForceLockWallHatchLayer(doc, parentId);
    return;
  }

  var concreteSet = new HashSet<Guid>(concreteAxisIds);

  // ------------------------------------------------------------
  // 1) 统计：当前 parentId 下，所有“钢筋混凝土轴线”的端点聚类（用于全/半封口判定）
  // ------------------------------------------------------------
  var endpointIndex = new Dictionary<PtKey, HashSet<Guid>>();

  int axisLi0 = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLi0 >= 0 && axisLi0 < doc.Layers.Count)
  {
    var axisLayer = doc.Layers[axisLi0];
    var axisObjs  = (axisLayer != null) ? doc.Objects.FindByLayer(axisLayer) : null;

    if (axisObjs != null)
    {
      foreach (var aobj in axisObjs)
      {
        if (aobj == null) continue;

        var acrv = aobj.Geometry as Curve;
        if (acrv == null) continue;

        string nm = aobj.Attributes?.Name ?? "";
        if (!nm.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
          continue;

        string wallTag = GetTagString(nm, "walltype", "concrete");
        WallKind kind = ParseWallKindTagOrDefault(wallTag);
        if (kind != WallKind.Concrete) continue;

        Guid aid = aobj.Id;

        IndexEndpoint(endpointIndex, acrv.PointAtStart, aid, grid);
        IndexEndpoint(endpointIndex, acrv.PointAtEnd,   aid, grid);
      }
    }
  }

  int CountConcreteAt(Point3d pt)
  {
    var set = new HashSet<Guid>();
    foreach (var id in QueryNearEndpoint(endpointIndex, pt, grid))
      set.Add(id);
    return set.Count;
  }

  // ------------------------------------------------------------
  // 2) 收集：本轮 concreteAxisIds 对应的左右墙线（按 AxisId+Side）
  // ------------------------------------------------------------
  var wallL = new Dictionary<Guid, Curve>();
  var wallR = new Dictionary<Guid, Curve>();

  var oes = NewEnumeratorAllCurves();
  foreach (var ro in doc.Objects.GetObjectList(oes))
  {
    if (ro == null) continue;

    int li = ro.Attributes.LayerIndex;
    if (!IsLayerUnderParent(doc, li, parentId))
      continue;

    var c = ro.Geometry as Curve;
    if (c == null || !c.IsValid) continue;

    string nm = ro.Attributes?.Name ?? "";
    if (!nm.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase))
      continue;

    Guid axisId;
    if (!TryParseAxisIdFromName(nm, out axisId))
      continue;

    if (!concreteSet.Contains(axisId))
      continue;

    bool isLeft, isRight;
    if (!TryGetSideFromName(nm, out isLeft, out isRight))
      continue;

    if (isLeft)  wallL[axisId] = c.DuplicateCurve();
    if (isRight) wallR[axisId] = c.DuplicateCurve();
  }

  // ------------------------------------------------------------
  // 3) 确保 Hatch 图层存在，并在生成期间临时解锁
  // ------------------------------------------------------------
  int hatchLi = EnsureWallHatchLayerIndex(doc, parentId);
  if (hatchLi < 0 || hatchLi >= doc.Layers.Count) return;

  bool hatchWasLocked = false;
  var hatchLay = doc.Layers[hatchLi];
  if (hatchLay != null && hatchLay.IsLocked)
  {
    hatchWasLocked = true;
    hatchLay.IsLocked = false;
    doc.Layers.Modify(hatchLay, hatchLi, true);
  }

  try
  {
    // 3.1) 先删掉本轮轴线的旧 Hatch（双保险：即便上游没删到，也避免重复）
    var allHatches = doc.Objects.FindByObjectType(ObjectType.Hatch);
    if (allHatches != null)
    {
      foreach (var hro in allHatches)
      {
        if (hro == null) continue;

        int hli = hro.Attributes.LayerIndex;
        if (!IsLayerUnderParent(doc, hli, parentId))
          continue;

        string hn = hro.Attributes?.Name ?? "";
        if (!hn.StartsWith("WallHatch|", StringComparison.OrdinalIgnoreCase))
          continue;

        Guid axisId;
        if (!TryParseAxisIdFromName(hn, out axisId))
          continue;

        if (!concreteSet.Contains(axisId))
          continue;

        // Hatch 可能在锁层：临时解锁
        var lay = (hli >= 0 && hli < doc.Layers.Count) ? doc.Layers[hli] : null;
        bool wasLocked = false;
        if (lay != null && lay.IsLocked)
        {
          wasLocked = true;
          lay.IsLocked = false;
          doc.Layers.Modify(lay, hli, true);
        }

        doc.Objects.Delete(hro, true);

        if (wasLocked && lay != null)
        {
          lay.IsLocked = true;
          doc.Layers.Modify(lay, hli, true);
        }
      }
    }

    // 3.2) Solid 填充图案（★ 1.5.4：新文档可能尚未预加载 HatchPatternTable；找不到就自动写入默认 Solid）
    int solidIndex = -1;

    try { solidIndex = doc.HatchPatterns.Find("Solid", true); } catch { solidIndex = -1; }
    if (solidIndex < 0) { try { solidIndex = doc.HatchPatterns.Find("实体", true); } catch { solidIndex = -1; } }
    if (solidIndex < 0) { try { solidIndex = doc.HatchPatterns.Find("SOLID", true); } catch { } }

    if (solidIndex < 0)
    {
      // ✅ 尝试把 Rhino 默认 Solid 图案写入文档（避免“必须先手动 hatch 一次”才能自动出填充）
      try
      {
        solidIndex = doc.HatchPatterns.Add(Rhino.DocObjects.HatchPattern.Defaults.Solid);
        if (solidIndex >= 0)
          RhinoApp.WriteLine("[Hatch] 文档未预加载 Solid 填充图案，已自动添加默认 Solid 图案。");
      }
      catch { solidIndex = -1; }
    }

    // 兜底：极少数版本里 Solid 可能是 hard-coded index=-1；这里继续尝试用 -1 创建（失败则该轴线跳过）
    if (solidIndex < 0)
    {
      RhinoApp.WriteLine("[Hatch] 未能在文档中找到/写入 Solid 图案，尝试使用 index=-1 创建 Solid Hatch（兼容兜底）。");
      solidIndex = -1;
    }

    // ----------------------------------------------------------
    // 4) 对每根钢筋混凝土轴线：拼闭合轮廓 → Create Hatch → Add
    // ----------------------------------------------------------
    foreach (var axisId in concreteAxisIds)
    {
      Curve axisCrv = null;
      var axisRo = doc.Objects.FindId(axisId);
      if (axisRo != null) axisCrv = axisRo.Geometry as Curve;
      if (axisCrv == null) continue;

      Curve wl0, wr0;
      if (!wallL.TryGetValue(axisId, out wl0)) continue;
      if (!wallR.TryGetValue(axisId, out wr0)) continue;

      // ★ 填充阶段：完全把“墙线起点=轴线起点同侧”当作前提
      //    不再做任何“最近点/距离比较”来判方向，而是按固定拓扑顺序拼闭合轮廓
      //    约定（按轴线 Start=A, End=B）：
      //      A = axS, B = axE
      //      AL = 左墙线起点, BL = 左墙线终点
      //      AR = 右墙线起点, BR = 右墙线终点
      //    固定拓扑：
      //      Full   ：LAL-L-LBL-LBR-R-LAR
      //      SkipA  ：LA-L-LBL-LBR-R
      //      SkipB  ：LAL-L-LB-R-LAR
      //      SkipA&B：LA-L-LB-R
      var wl = wl0.DuplicateCurve();
      var wr = wr0.DuplicateCurve();
      var axS = axisCrv.PointAtStart; // A
      var axE = axisCrv.PointAtEnd;   // B

      if (wl == null || wr == null || !wl.IsValid || !wr.IsValid) continue;

      // 前提：wl.Start=AL, wl.End=BL；wr.Start=AR, wr.End=BR（不再反转校正）
      var AL = wl.PointAtStart;
      var BL = wl.PointAtEnd;
      var AR = wr.PointAtStart;
      var BR = wr.PointAtEnd;

      bool fullS = (CountConcreteAt(axS) >= 2); // true=不跳过 A
      bool fullE = (CountConcreteAt(axE) >= 2); // true=不跳过 B

      bool skipA = !fullS;
      bool skipB = !fullE;

      // 右墙线在轮廓中总是用“BR -> AR”（即 wr 的反向）
      Curve Rrev = null;
      try { Rrev = wr.DuplicateCurve(); } catch { Rrev = null; }
      if (Rrev == null || !Rrev.IsValid) continue;
      Rrev.Reverse();

      var segs = new List<Curve>();

      if (!skipA && !skipB)
      {
        // A -> AL -> BL -> B -> BR -> AR -> A
        segs.Add(new LineCurve(axS, AL));  // LAL
        segs.Add(wl);                      // L (AL->BL)
        segs.Add(new LineCurve(BL, axE));  // LBL
        segs.Add(new LineCurve(axE, BR));  // LBR
        segs.Add(Rrev);                    // R (BR->AR)
        segs.Add(new LineCurve(AR, axS));  // LAR
      }
      else if (skipA && !skipB)
      {
        // AR -> AL -> BL -> B -> BR -> AR
        segs.Add(new LineCurve(AR, AL));   // LA（方向取 AR->AL）
        segs.Add(wl);                      // L
        segs.Add(new LineCurve(BL, axE));  // LBL
        segs.Add(new LineCurve(axE, BR));  // LBR
        segs.Add(Rrev);                    // R
      }
      else if (!skipA && skipB)
      {
        // A -> AL -> BL -> BR -> AR -> A
        segs.Add(new LineCurve(axS, AL));  // LAL
        segs.Add(wl);                      // L
        segs.Add(new LineCurve(BL, BR));   // LB（跳过 B）
        segs.Add(Rrev);                    // R
        segs.Add(new LineCurve(AR, axS));  // LAR
      }
      else // skipA && skipB
      {
        // AR -> AL -> BL -> BR -> AR
        segs.Add(new LineCurve(AR, AL));   // LA
        segs.Add(wl);                      // L
        segs.Add(new LineCurve(BL, BR));   // LB
        segs.Add(Rrev);                    // R
      }

      // Join 成单一闭合轮廓
      var joined = Curve.JoinCurves(segs, joinTol);
      Curve loop = null;
      if (joined != null && joined.Length > 0)
      {
        foreach (var jc in joined)
        {
          if (jc != null && jc.IsValid && jc.IsClosed)
          {
            loop = jc;
            break;
          }
        }
      }

      if (loop == null || !loop.IsValid || !loop.IsClosed)
        continue;

      var hatches = Hatch.Create(loop, solidIndex, 0.0, 1.0);
      if (hatches == null || hatches.Length == 0)
        continue;

      var hatch = hatches[0];

      var attr = new ObjectAttributes();
      attr.LayerIndex = hatchLi;
      attr.Name       = "WallHatch|AxisId=" + axisId.ToString();

      doc.Objects.AddHatch(hatch, attr);
    }
  }
  finally
  {
    // ★ 无论如何：强制锁回 Hatch 图层
    var lay = doc.Layers[hatchLi];
    if (lay != null && !lay.IsLocked)
    {
      lay.IsLocked = true;
      doc.Layers.Modify(lay, hatchLi, true);
    }
    ForceLockWallHatchLayer(doc, parentId);
  }
}

// ===== 9) 墙体导角（r=0） =====（用于节点排序的小结构）
// 这里放在文件作用域，避免 RhinoScript Editor 不支持方法内声明类型。
class AxisEndAtNode
{
  public Guid AxisId;
  public bool IsStart;          // 节点是否是 Axis 的起点
  public int  VectorDir;        // 1=驶离节点（Start 在节点），-1=对着节点（End 在节点）
  public Vector3d OutDir;       // 统一“从节点指向轴线内部”的方向（用于排序）
  public double Thickness;      // OffsetL+OffsetR
  public Curve AxisCurve;       // 轴线曲线（快照）
}


// ===== 9) 墙体导角（r=0） =====
// 说明：
// - 只处理 RTZ3State.axistobedeal 里端点“同点重复”形成的节点（同父级 parentId）；
// - 节点内按“12点起顺时针”排序轴线；相邻两轴线形成 ChamferPair；
// - 直线墙线：只记录交点 A，最后统一用 LineCurve 重建（9.13）；
// - 圆弧/样条墙线：只延长一个端点（9.11），在延长线上求交点后 Trim 到 A（9.12）；
// - 若某对墙线都是直线，且两条【轴线】在节点处曲率连续，则跳过这一对（9.15）；
// - 保温线：若两侧都有保温，则保温-保温导角；若只有一侧有保温，则该保温与对侧墙线导角（只改保温）（9.16）；
// - 最终同步墙线/保温线 Name 内的 Start/End 坐标（9.17）。
void ApplyWallChamferForAxesToBeDeal(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return;

  var axisIds = RTZ3State.axistobedeal;
  if (axisIds == null || axisIds.Count == 0)
  {
    RhinoApp.WriteLine("[Chamfer] axistobedeal 为空，跳过导角。");
    return;
  }

  double tol = doc.ModelAbsoluteTolerance;

  // ★ 端点连接判定用更宽松容差（默认至少 1mm）
  double mm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  double connectTol = Math.Max(tol, 1.0 * mm);
  double grid = connectTol;

  // 交点计算容差（略放宽）
  double tolInt = Math.Max(tol * 5.0, connectTol);

  string Key(Guid axisId, string sideLower)
  {
    return axisId.ToString("N") + "|" + sideLower;
  }

  bool IsValidWallaxisName(string name)
  {
    if (string.IsNullOrWhiteSpace(name)) return false;
    return name.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase);
  }

  // 只看同父级的轴线层
  int axisLayerIndex = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLayerIndex < 0)
  {
    RhinoApp.WriteLine("[Chamfer] 未找到 PB-Wall-Axis 图层，跳过导角。");
    return;
  }

  Layer axisLayer = doc.Layers[axisLayerIndex];
  if (axisLayer == null) return;

  // ---------- 0) 收集同父级 parentId 下的所有轴线（排除 Name 为空/非 Wallaxis） ----------
  var axisCurveMap = new Dictionary<Guid, Curve>();
  var axisThickMap = new Dictionary<Guid, double>(); // AxisId → Thickness

  var axisKindMap  = new Dictionary<Guid, WallKind>(); // AxisId → WallKind
  var axisLevelMap = new Dictionary<Guid, int>();      // AxisId → Level

  var axisObjsAll = doc.Objects.FindByLayer(axisLayer);
  if (axisObjsAll == null || axisObjsAll.Length == 0) return;

  foreach (var ro in axisObjsAll)
  {
    if (ro == null) continue;
    if (!(ro is CurveObject)) continue;
    if (ro.Attributes == null) continue;
    if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId)) continue;

    string nm = ro.Attributes.Name ?? "";
    if (!IsValidWallaxisName(nm)) continue;

    var cobj = ro as CurveObject;
    if (cobj == null) continue;

    var crv = cobj.Geometry as Curve;
    if (crv == null) continue;

    Guid id = ro.Id;
    axisCurveMap[id] = crv.DuplicateCurve();

    // 厚度：从 AxisInfo 解析（失败则用 200 兜底）
    var info = new AxisInfo();
    info.AxisId = id;
    if (!FillAxisInfoFromName(nm, info))
    {
      // 解析失败：厚度兜底 200，并默认当作 Block（填充墙）
      axisThickMap[id] = 200.0;
      axisKindMap[id]  = WallKind.Block;
      axisLevelMap[id] = GetWallKindLevel(WallKind.Block);
    }
    else
    {
      double t = Math.Abs(info.OffsetL) + Math.Abs(info.OffsetR);
      if (t < tol) t = 200.0;
      axisThickMap[id] = t;

      axisKindMap[id]  = info.WallType;
      axisLevelMap[id] = GetWallKindLevel(info.WallType);
    }
  }

  if (axisCurveMap.Count == 0) return;

  // ---------- 1) 构建端点索引：PtKey → List<AxisEndAtNode> ----------
  var endpointMap = new Dictionary<PtKey, List<AxisEndAtNode>>();

  void AddEndpoint(Guid axisId, Curve axisCrv, bool isStart)
  {
    if (axisCrv == null) return;

    Point3d p = isStart ? axisCrv.PointAtStart : axisCrv.PointAtEnd;
    var k = PtKey.FromPoint(p, grid);

    List<AxisEndAtNode> list;
    if (!endpointMap.TryGetValue(k, out list))
    {
      list = new List<AxisEndAtNode>();
      endpointMap[k] = list;
    }

    // 去重（同一轴线同一端点只加一次）
    for (int i = 0; i < list.Count; i++)
    {
      if (list[i].AxisId == axisId && list[i].IsStart == isStart)
        return;
    }

    int vdir = isStart ? 1 : -1;

    // ★ OutDir：用轴线在端点处的切线，再乘 VectorDir 统一为“从节点指向轴线内部”
    Vector3d tan = Vector3d.Zero;
    if (isStart)
      tan = axisCrv.TangentAt(axisCrv.Domain.T0);
    else
      tan = axisCrv.TangentAt(axisCrv.Domain.T1);

    Vector3d outDir = tan * vdir;
    outDir.Z = 0.0;
    if (!outDir.Unitize())
    {
      // 兜底：用端点指向另一端的向量
      Vector3d vv = (isStart ? (axisCrv.PointAtEnd - axisCrv.PointAtStart) : (axisCrv.PointAtStart - axisCrv.PointAtEnd));
      vv.Z = 0.0;
      outDir = vv;
      outDir.Unitize();
    }

    double thick = 200.0;
    axisThickMap.TryGetValue(axisId, out thick);

    list.Add(new AxisEndAtNode
    {
      AxisId    = axisId,
      IsStart   = isStart,
      VectorDir = vdir,
      OutDir    = outDir,
      Thickness = thick,
      AxisCurve = axisCrv
    });
  }

  foreach (var kv in axisCurveMap)
  {
    AddEndpoint(kv.Key, kv.Value, true);
    AddEndpoint(kv.Key, kv.Value, false);
  }

  // ---------- 2) 找到要处理的“节点”：仅以 axistobedeal 的端点触发（重复次数>1） ----------
  var nodeKeys = new HashSet<PtKey>();
  foreach (var id in axisIds)
  {
    Curve ax;
    if (!axisCurveMap.TryGetValue(id, out ax)) continue;

    var k0 = PtKey.FromPoint(ax.PointAtStart, grid);
    var k1 = PtKey.FromPoint(ax.PointAtEnd, grid);

    List<AxisEndAtNode> list0;
    if (endpointMap.TryGetValue(k0, out list0) && list0 != null && list0.Count >= 2)
      nodeKeys.Add(k0);

    List<AxisEndAtNode> list1;
    if (endpointMap.TryGetValue(k1, out list1) && list1 != null && list1.Count >= 2)
      nodeKeys.Add(k1);
  }

  if (nodeKeys.Count == 0)
  {
    RhinoApp.WriteLine("[Chamfer] 未发现端点重复节点，跳过导角。");
    return;
  }

  // ---------- 3) 构建 wall / wallinside / insul 映射：AxisId+Side → ObjectId ----------
  var wallMap       = new Dictionary<string, Guid>();
  var wallInsideMap = new Dictionary<string, Guid>();
  var insulMap      = new Dictionary<string, Guid>();

  var oes = new ObjectEnumeratorSettings
  {
    ObjectTypeFilter = ObjectType.Curve,
    IncludeLights = false,
    IncludeGrips = false
  };

  var curveObjsAll = doc.Objects.GetObjectList(oes);
  if (curveObjsAll != null)
  {
    foreach (var ro in curveObjsAll)
    {
      if (ro == null) continue;
      if (!(ro is CurveObject)) continue;
      if (ro.Attributes == null) continue;
      if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId)) continue;

      string nm = ro.Attributes.Name ?? "";
      if (string.IsNullOrWhiteSpace(nm)) continue;

      if (nm.StartsWith("Wallinside|", StringComparison.OrdinalIgnoreCase))
      {
        // ★ 1.1.2：玻璃幕墙内墙线（Wallinside）
        string axisStr = GetTagString(nm, "AxisId", "");
        string sideStr = GetTagString(nm, "Side", "");

        Guid axisId;
        if (!Guid.TryParse(axisStr, out axisId)) continue;

        string side = (sideStr ?? "").Trim().ToLowerInvariant();
        if (side != "left" && side != "right") continue;

        wallInsideMap[Key(axisId, side)] = ro.Id;
        continue;
      }

      if (nm.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase))
      {
        string axisStr = GetTagString(nm, "AxisId", "");
        string sideStr = GetTagString(nm, "Side", "");

        Guid axisId;
        if (!Guid.TryParse(axisStr, out axisId)) continue;

        string side = (sideStr ?? "").Trim().ToLowerInvariant();
        if (side != "left" && side != "right") continue;

        wallMap[Key(axisId, side)] = ro.Id;
      }
      else if (nm.StartsWith("Insulation|", StringComparison.OrdinalIgnoreCase))
      {
        string axisStr = GetTagString(nm, "AxisId", "");
        string sideStr = GetTagString(nm, "Side", "");

        Guid axisId;
        if (!Guid.TryParse(axisStr, out axisId)) continue;

        string side = (sideStr ?? "").Trim().ToLowerInvariant();
        if (side != "left" && side != "right") continue;

        insulMap[Key(axisId, side)] = ro.Id;
      }
    }
  }

  // ---------- 4) 锁层处理：Replace/ModifyName 时需要临时解锁 ----------
  var layerLockBackup = new Dictionary<int, bool>();

  void EnsureUnlockedByObjId(Guid objId)
  {
    var ro = doc.Objects.FindId(objId);
    if (ro == null || ro.Attributes == null) return;

    int li = ro.Attributes.LayerIndex;
    if (li < 0 || li >= doc.Layers.Count) return;

    if (!layerLockBackup.ContainsKey(li))
      layerLockBackup[li] = doc.Layers[li].IsLocked;

    if (doc.Layers[li].IsLocked)
      doc.Layers[li].IsLocked = false;
  }

  void RestoreLayerLocks()
  {
    foreach (var kv in layerLockBackup)
    {
      int li = kv.Key;
      if (li >= 0 && li < doc.Layers.Count)
        doc.Layers[li].IsLocked = kv.Value;
    }
    layerLockBackup.Clear();
  }

  // ---------- 5) 直线待统一重建：ObjId → (Start/End → Point) ----------
  var lineHitWall  = new Dictionary<Guid, Dictionary<CurveEnd, Point3d>>();
  var lineHitInsul = new Dictionary<Guid, Dictionary<CurveEnd, Point3d>>();

  void RecordLineHit(Dictionary<Guid, Dictionary<CurveEnd, Point3d>> hitMap, Guid objId, CurveEnd end, Point3d pt)
  {
    Dictionary<CurveEnd, Point3d> ends;
    if (!hitMap.TryGetValue(objId, out ends))
    {
      ends = new Dictionary<CurveEnd, Point3d>();
      hitMap[objId] = ends;
    }

    // 同一端点只保留一个：若已有则取更接近该端点的
    var ro = doc.Objects.FindId(objId) as CurveObject;
    var crv = ro?.Geometry as Curve;
    if (crv == null)
    {
      ends[end] = pt;
      return;
    }

    Point3d anchor = (end == CurveEnd.Start) ? crv.PointAtStart : crv.PointAtEnd;

    Point3d old;
    if (ends.TryGetValue(end, out old))
    {
      if (pt.DistanceToSquared(anchor) < old.DistanceToSquared(anchor))
        ends[end] = pt;
    }
    else
    {
      ends[end] = pt;
    }
  }

  bool IsLine(Curve crv)
  {
    if (crv == null) return false;
    if (crv is LineCurve) return true;
    return crv.IsLinear(tolInt);
  }

  bool IsArc(Curve crv)
  {
    if (crv == null) return false;
    if (crv is ArcCurve) return true;
    Arc arc;
    return crv.TryGetArc(out arc);
  }

  // 延长长度限制：Arc 不能超过“剩余周长”；Spline 不能超过 1000
  double ClampExtendLen(Curve crv, double len)
  {
    if (len <= 0) return 0.0;
    if (crv == null) return 0.0;

    if (IsArc(crv))
    {
      Arc arc;
      if (crv.TryGetArc(out arc))
      {
        double r = arc.Radius;
        if (r > tolInt)
        {
          double perim = 2.0 * Math.PI * r;
          double arcLen = crv.GetLength();
          double remain = perim - arcLen - tolInt;
          if (remain < 0) remain = 0;
          return Math.Min(len, remain);
        }
      }
    }

    // spline / nurbs：限制 1000
    if (!IsLine(crv) && !IsArc(crv))
      return Math.Min(len, 1000.0);

    return len;
  }

  Curve ExtendForHit(Curve src, CurveEnd mainEnd, double mainLen, double backLen)
  {
    if (src == null) return null;

    var crv = src.DuplicateCurve();
    if (crv == null) return null;

    // 只用于求交：主端点延长 15×厚度，反向再延长 0.5×厚度
    CurveExtensionStyle style = CurveExtensionStyle.Smooth;
    if (IsLine(crv)) style = CurveExtensionStyle.Line;
    else if (IsArc(crv)) style = CurveExtensionStyle.Arc;

    double l0 = ClampExtendLen(crv, mainLen);
    double l1 = ClampExtendLen(crv, backLen);

    if (l0 > tolInt)
    {
      var ext = crv.Extend(mainEnd, l0, style);
      if (ext != null) crv = ext;
    }

    CurveEnd otherEnd = (mainEnd == CurveEnd.Start) ? CurveEnd.End : CurveEnd.Start;
    if (l1 > tolInt)
    {
      var ext2 = crv.Extend(otherEnd, l1, style);
      if (ext2 != null) crv = ext2;
    }

    return crv;
  }

  Curve ExtendForFinal(Curve src, CurveEnd mainEnd, double mainLen)
  {
    if (src == null) return null;
    var crv = src.DuplicateCurve();
    if (crv == null) return null;

    CurveExtensionStyle style = CurveExtensionStyle.Smooth;
    if (IsLine(crv)) style = CurveExtensionStyle.Line;
    else if (IsArc(crv)) style = CurveExtensionStyle.Arc;

    double l0 = ClampExtendLen(crv, mainLen);
    if (l0 > tolInt)
    {
      var ext = crv.Extend(mainEnd, l0, style);
      if (ext != null) crv = ext;
    }

    return crv;
  }

  bool TryPickIntersectionNearNode(Curve a, Curve b, Point3d node, out Point3d hit)
  {
    hit = Point3d.Unset;
    if (a == null || b == null) return false;

    var x = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, tolInt, tolInt);
    if (x == null || x.Count == 0) return false;

    double bestD2 = double.MaxValue;
    Point3d best = Point3d.Unset;

    for (int i = 0; i < x.Count; i++)
    {
      var ev = x[i];
      if (!ev.IsPoint) continue;

      Point3d p = ev.PointA;
      double d2 = p.DistanceToSquared(node);
      if (d2 < bestD2)
      {
        bestD2 = d2;
        best = p;
      }
    }

    if (!best.IsValid) return false;

    hit = best;
    return true;
  }

  Curve TrimToPoint(Curve srcFinal, CurveEnd mainEnd, Point3d hitPt)
  {
    if (srcFinal == null) return null;

    double t;
    if (!srcFinal.ClosestPoint(hitPt, out t, tolInt))
      return null;

    var dom = srcFinal.Domain;
    double t0 = dom.T0;
    double t1 = dom.T1;

    if (t < t0) t = t0;
    if (t > t1) t = t1;

    if (mainEnd == CurveEnd.Start)
    {
      // 保留 [t, t1]
      if (t >= t1 - tolInt) return null;
      var trimmed = srcFinal.Trim(new Interval(t, t1));
      return trimmed;
    }
    else
    {
      // 保留 [t0, t]
      if (t <= t0 + tolInt) return null;
      var trimmed = srcFinal.Trim(new Interval(t0, t));
      return trimmed;
    }
  }

  void SyncObjStartEndName(Guid objId)
  {
    var ro = doc.Objects.FindId(objId);
    var cobj = ro as CurveObject;
    var crv = cobj?.Geometry as Curve;
    if (ro == null || ro.Attributes == null || crv == null) return;

    string name = ro.Attributes.Name ?? "";
    string name2 = SetOrAppendTag(name, "Start", FormatPoint(crv.PointAtStart));
    name2 = SetOrAppendTag(name2, "End",   FormatPoint(crv.PointAtEnd));

    ModifyObjectName(doc, ro, name2);
  }

  // 处理一对曲线：根据 modifyA/modifyB 决定是否真正改动
  void ProcessPair(
    Point3d node,
    AxisEndAtNode axA, string sideA, Guid objA, bool modifyA,
    AxisEndAtNode axB, string sideB, Guid objB, bool modifyB,
    bool isInsulationPair)
  {
    if (objA == Guid.Empty || objB == Guid.Empty) return;

    var coA = doc.Objects.FindId(objA) as CurveObject;
    var coB = doc.Objects.FindId(objB) as CurveObject;
    var crvA0 = coA?.Geometry as Curve;
    var crvB0 = coB?.Geometry as Curve;

    if (crvA0 == null || crvB0 == null) return;

    // ★ 两条墙线都是直线时：先判断对应两条【轴线】在此点是否曲率连续，连续则跳过
    if (!isInsulationPair && IsLine(crvA0) && IsLine(crvB0))
    {
      var e0 = new JoinEndpointInfo();
      e0.Curve = axA.AxisCurve;
      e0.T = axA.IsStart ? axA.AxisCurve.Domain.T0 : axA.AxisCurve.Domain.T1;

      var e1 = new JoinEndpointInfo();
      e1.Curve = axB.AxisCurve;
      e1.T = axB.IsStart ? axB.AxisCurve.Domain.T0 : axB.AxisCurve.Domain.T1;

      if (IsCurvatureContinuousAtSharedPoint(e0, e1, tolInt))
      {
        return; // 只跳过这一对
      }
    }

    CurveEnd endA = (axA.VectorDir == 1) ? CurveEnd.Start : CurveEnd.End;
    CurveEnd endB = (axB.VectorDir == 1) ? CurveEnd.Start : CurveEnd.End;

    double extMainA = axA.Thickness * 15.0;
    double extBackA = axA.Thickness * 0.5;
    double extMainB = axB.Thickness * 15.0;
    double extBackB = axB.Thickness * 0.5;

    // 先在“延长线”上求交点
    var extA = ExtendForHit(crvA0, endA, extMainA, extBackA);
    var extB = ExtendForHit(crvB0, endB, extMainB, extBackB);
    if (extA == null || extB == null) return;

    Point3d hit;
    if (!TryPickIntersectionNearNode(extA, extB, node, out hit))
      return;

    // A) 处理 A
    if (modifyA)
    {
      if (IsLine(crvA0))
      {
        var map = isInsulationPair ? lineHitInsul : lineHitWall;
        RecordLineHit(map, objA, endA, hit);
      }
      else
      {
        var finalA = ExtendForFinal(crvA0, endA, extMainA);
        var trimmedA = TrimToPoint(finalA, endA, hit);
        if (trimmedA != null)
        {
          EnsureUnlockedByObjId(objA);
          if (doc.Objects.Replace(objA, trimmedA))
            SyncObjStartEndName(objA);
        }
      }
    }

    // B) 处理 B
    if (modifyB)
    {
      if (IsLine(crvB0))
      {
        var map = isInsulationPair ? lineHitInsul : lineHitWall;
        RecordLineHit(map, objB, endB, hit);
      }
      else
      {
        var finalB = ExtendForFinal(crvB0, endB, extMainB);
        var trimmedB = TrimToPoint(finalB, endB, hit);
        if (trimmedB != null)
        {
          EnsureUnlockedByObjId(objB);
          if (doc.Objects.Replace(objB, trimmedB))
            SyncObjStartEndName(objB);
        }
      }
    }
  }

  // ---------- 5.5) 墙线特殊导角调度（10.x） ----------
  double ClockAngle(AxisEndAtNode ax)
  {
    double a = Math.Atan2(ax.OutDir.X, ax.OutDir.Y);
    if (a < 0) a += 2.0 * Math.PI;
    return a;
  }

  void SortEndsClockwise(List<AxisEndAtNode> list)
  {
    if (list == null || list.Count < 2) return;
    list.Sort((a, b) =>
    {
      double aa = ClockAngle(a);
      double bb = ClockAngle(b);
      return aa.CompareTo(bb);
    });
  }

  string CwSide(AxisEndAtNode ax)
  {
    return (ax.VectorDir == 1) ? "right" : "left";
  }

  string CcwSide(AxisEndAtNode ax)
  {
    return (ax.VectorDir == 1) ? "left" : "right";
  }

  WallKind GetAxisKind(Guid axisId)
  {
    WallKind k;
    if (!axisKindMap.TryGetValue(axisId, out k))
      k = WallKind.Block;
    return k;
  }

  int GetAxisLevel(Guid axisId)
  {
    int lv;
    if (!axisLevelMap.TryGetValue(axisId, out lv))
      lv = GetWallKindLevel(GetAxisKind(axisId));
    return lv;
  }

  bool TryGetWallObj(Guid axisId, string sideLower, out Guid objId)
  {
    objId = Guid.Empty;
    if (string.IsNullOrEmpty(sideLower)) return false;
    return wallMap.TryGetValue(Key(axisId, sideLower), out objId) && objId != Guid.Empty;
  }

  bool TryGetWallinsideObj(Guid axisId, string sideLower, out Guid objId)
  {
    objId = Guid.Empty;
    if (string.IsNullOrEmpty(sideLower)) return false;
    return wallInsideMap.TryGetValue(Key(axisId, sideLower), out objId) && objId != Guid.Empty;
  }

  // ★ 取“内墙线优先，否则外墙线”，并返回该对象是否为内墙线
  bool TryGetWallinsideOrWallObj(Guid axisId, string sideLower, out Guid objId, out bool isInside)
  {
    objId = Guid.Empty;
    isInside = false;
    if (string.IsNullOrEmpty(sideLower)) return false;

    if (TryGetWallinsideObj(axisId, sideLower, out objId))
    {
      isInside = true;
      return true;
    }

    if (TryGetWallObj(axisId, sideLower, out objId))
    {
      isInside = false;
      return true;
    }

    return false;
  }

  List<AxisEndAtNode> BuildSubsetEnds(List<AxisEndAtNode> allEnds, HashSet<Guid> axisIdsSet)
  {
    var list = new List<AxisEndAtNode>();
    if (allEnds == null || axisIdsSet == null || axisIdsSet.Count == 0) return list;

    for (int i = 0; i < allEnds.Count; i++)
    {
      var ax = allEnds[i];
      if (axisIdsSet.Contains(ax.AxisId))
        list.Add(ax);
    }

    SortEndsClockwise(list);
    return list;
  }

  // ---------------- 外墙线：Normal/Target/Rank ----------------

  void WallChamferNormal(Point3d node, List<AxisEndAtNode> list)
  {
    if (list == null) return;
    int c = list.Count;
    if (c < 2) return;

    for (int i = 0; i < c; i++)
    {
      var axA = list[i];
      var axB = list[(i + 1) % c];

      string sideA = CwSide(axA);
      string sideB = CcwSide(axB);

      Guid wallAId, wallBId;
      if (!TryGetWallObj(axA.AxisId, sideA, out wallAId)) continue;
      if (!TryGetWallObj(axB.AxisId, sideB, out wallBId)) continue;

      ProcessPair(node, axA, sideA, wallAId, true, axB, sideB, wallBId, true, false);
    }
  }

  void WallChamferTarget(Point3d node, List<AxisEndAtNode> list, HashSet<Guid> targets)
  {
    if (list == null) return;
    if (targets == null || targets.Count == 0) return;

    int c = list.Count;
    if (c < 2) return;

    for (int i = 0; i < c; i++)
    {
      var axA = list[i];
      var axB = list[(i + 1) % c];

      bool modA = targets.Contains(axA.AxisId);
      bool modB = targets.Contains(axB.AxisId);
      if (!modA && !modB) continue;

      string sideA = CwSide(axA);
      string sideB = CcwSide(axB);

      Guid wallAId, wallBId;
      if (!TryGetWallObj(axA.AxisId, sideA, out wallAId)) continue;
      if (!TryGetWallObj(axB.AxisId, sideB, out wallBId)) continue;

      ProcessPair(node, axA, sideA, wallAId, modA, axB, sideB, wallBId, modB, false);
    }
  }
  // ---------- 5.5.x) 新增：自身缩短导角（Self-Shorten Chamfer） ----------
  // 规则：
  // - 仅在“同一节点”且 list.Count>=3 时触发；
  // - 对目标墙线做两次“自身导角”（用两侧相邻的正常配对墙线分别做帮忙墙线），取更短者；
  // - 自身缩短导角过程中：禁止反向延长（back extend=0）。
  bool TryComputeSelfChamferCandidateShorten(
    Point3d node,
    AxisEndAtNode axT, Guid objT,
    AxisEndAtNode axH, Guid objH,
    out double outLen,
    out Point3d outHit,
    out Curve outTrimmed)
  {
    outLen = double.PositiveInfinity;
    outHit = Point3d.Unset;
    outTrimmed = null;

    if (objT == Guid.Empty || objH == Guid.Empty) return false;

    var coT = doc.Objects.FindId(objT) as CurveObject;
    var coH = doc.Objects.FindId(objH) as CurveObject;
    var crvT0 = coT?.Geometry as Curve;
    var crvH0 = coH?.Geometry as Curve;
    if (crvT0 == null || crvH0 == null) return false;

    CurveEnd endT = (axT.VectorDir == 1) ? CurveEnd.Start : CurveEnd.End;
    CurveEnd endH = (axH.VectorDir == 1) ? CurveEnd.Start : CurveEnd.End;

    // ★ 自身缩短导角：只沿节点端延长（禁止反向延长）
    double extMainT_short = axT.Thickness * 15.0;
    double extBackT_short = 0.0;
    double extMainH_short = axH.Thickness * 15.0;
    double extBackH_short = 0.0;

    var extT = ExtendForHit(crvT0, endT, extMainT_short, extBackT_short);
    var extH = ExtendForHit(crvH0, endH, extMainH_short, extBackH_short);
    if (extT == null || extH == null) return false;

    Point3d hit;
    if (!TryPickIntersectionNearNode(extT, extH, node, out hit))
      return false;

    // 注意：失败绝不能用 0 长度“胜出”，所以无效直接 return false
    if (IsLine(crvT0))
    {
      Point3d other = (endT == CurveEnd.Start) ? crvT0.PointAtEnd : crvT0.PointAtStart;
      double L = other.DistanceTo(hit);
      if (L <= tolInt) return false;

      outHit = hit;
      outLen = L;
      outTrimmed = null;
      return true;
    }
    else
    {
      var finalT = ExtendForFinal(crvT0, endT, extMainT_short);
      var trimmed = TrimToPoint(finalT, endT, hit);
      if (trimmed == null || !trimmed.IsValid) return false;

      double L = 0.0;
      try { L = trimmed.GetLength(); } catch { L = 0.0; }
      if (L <= tolInt) return false;

      outHit = hit;
      outLen = L;
      outTrimmed = trimmed;
      return true;
    }
  }

  void ApplySelfShortenToObj(
    Point3d node,
    AxisEndAtNode axT, Guid objT,
    AxisEndAtNode axH1, Guid objH1,
    AxisEndAtNode axH2, Guid objH2)
  {
    if (objT == Guid.Empty) return;

    double l1, l2;
    Point3d h1, h2;
    Curve t1, t2;

    bool ok1 = TryComputeSelfChamferCandidateShorten(node, axT, objT, axH1, objH1, out l1, out h1, out t1);
    bool ok2 = TryComputeSelfChamferCandidateShorten(node, axT, objT, axH2, objH2, out l2, out h2, out t2);

    if (!ok1 && !ok2) return;

    bool use1 = ok1 && (!ok2 || l1 <= l2);

    Point3d chosenHit = use1 ? h1 : h2;
    Curve chosenTrim  = use1 ? t1 : t2;

    var coT = doc.Objects.FindId(objT) as CurveObject;
    var crvT0 = coT?.Geometry as Curve;
    if (crvT0 == null) return;

    CurveEnd endT = (axT.VectorDir == 1) ? CurveEnd.Start : CurveEnd.End;

    if (IsLine(crvT0))
    {
      // 直线：只记录交点，最后统一重建（与普通导角一致）
      RecordLineHit(lineHitWall, objT, endT, chosenHit);
    }
    else
    {
      if (chosenTrim == null || !chosenTrim.IsValid) return;

      EnsureUnlockedByObjId(objT);
      if (doc.Objects.Replace(objT, chosenTrim))
        SyncObjStartEndName(objT);
    }
  }

  void WallChamferTargetShorten(Point3d node, List<AxisEndAtNode> list, HashSet<Guid> targets)
  {
    if (list == null) return;
    if (targets == null || targets.Count == 0) return;

    int c = list.Count;
    if (c < 3)
    {
      // 不满足触发条件：退回原有“自身导角/普通导角”逻辑
      WallChamferTarget(node, list, targets);
      return;
    }

    // list 已按顺时针排序
    for (int i = 0; i < c; i++)
    {
      var ax = list[i];
      if (!targets.Contains(ax.AxisId)) continue;

      var prev = list[(i - 1 + c) % c];
      var next = list[(i + 1) % c];

      // 1) 顺时针侧（与 next 配对的那条墙线）
      string sideT = CwSide(ax);
      Guid objT;
      if (TryGetWallObj(ax.AxisId, sideT, out objT))
      {
        Guid objH1 = Guid.Empty, objH2 = Guid.Empty;
        TryGetWallObj(next.AxisId, CcwSide(next), out objH1); // 正常配对对象
        TryGetWallObj(prev.AxisId, CwSide(prev),  out objH2); // 另一侧的正常配对对象
        ApplySelfShortenToObj(node, ax, objT, next, objH1, prev, objH2);
      }

      // 2) 逆时针侧（与 prev 配对的那条墙线）
      sideT = CcwSide(ax);
      if (TryGetWallObj(ax.AxisId, sideT, out objT))
      {
        Guid objH1 = Guid.Empty, objH2 = Guid.Empty;
        TryGetWallObj(prev.AxisId, CwSide(prev),   out objH1);
        TryGetWallObj(next.AxisId, CcwSide(next),  out objH2);
        ApplySelfShortenToObj(node, ax, objT, prev, objH1, next, objH2);
      }
    }
  }


  void WallChamferRank(Point3d node, AxisEndAtNode ax0, AxisEndAtNode ax1)
  {
    int lv0 = GetAxisLevel(ax0.AxisId);
    int lv1 = GetAxisLevel(ax1.AxisId);

    double a0 = ClockAngle(ax0);
    double a1 = ClockAngle(ax1);

    double d01 = a1 - a0;
    if (d01 < 0) d01 += 2.0 * Math.PI;
    double d10 = a0 - a1;
    if (d10 < 0) d10 += 2.0 * Math.PI;

    double dSmall = Math.Min(d01, d10);

    const double AngTol = 1e-6;
    if (dSmall < AngTol) return;
    if (Math.Abs(dSmall - Math.PI) < AngTol) return;

    bool smallIs01 = (d01 <= d10);

    string ax0Small, ax0Large, ax1Small, ax1Large;
    if (smallIs01)
    {
      ax0Small = CwSide(ax0);
      ax0Large = CcwSide(ax0);

      ax1Small = CcwSide(ax1);
      ax1Large = CwSide(ax1);
    }
    else
    {
      ax1Small = CwSide(ax1);
      ax1Large = CcwSide(ax1);

      ax0Small = CcwSide(ax0);
      ax0Large = CwSide(ax0);
    }

    AxisEndAtNode hi = (lv0 >= lv1) ? ax0 : ax1;
    AxisEndAtNode lo = (hi.AxisId == ax0.AxisId) ? ax1 : ax0;

    string hiSmall = (hi.AxisId == ax0.AxisId) ? ax0Small : ax1Small;
    string hiLarge = (hi.AxisId == ax0.AxisId) ? ax0Large : ax1Large;

    string loSmall = (lo.AxisId == ax0.AxisId) ? ax0Small : ax1Small;
    string loLarge = (lo.AxisId == ax0.AxisId) ? ax0Large : ax1Large;

    // 1) 小角侧：高级(小) + 低级(大) → 普通导角（两条都改）
    Guid hiSmallId, loLargeId;
    if (TryGetWallObj(hi.AxisId, hiSmall, out hiSmallId) &&
        TryGetWallObj(lo.AxisId, loLarge, out loLargeId))
    {
      ProcessPair(node, hi, hiSmall, hiSmallId, true, lo, loLarge, loLargeId, true, false);
    }

    // 2) 小角侧：低级(小) 自身导角，配对对象=高级(小)
    Guid loSmallId;
    if (TryGetWallObj(lo.AxisId, loSmall, out loSmallId) &&
        TryGetWallObj(hi.AxisId, hiSmall, out hiSmallId))
    {
      ProcessPair(node, lo, loSmall, loSmallId, true, hi, hiSmall, hiSmallId, false, false);
    }

    // 3) 大角侧：高级(大) 自身导角，配对对象=低级(大)
    Guid hiLargeId;
    if (TryGetWallObj(hi.AxisId, hiLarge, out hiLargeId) &&
        TryGetWallObj(lo.AxisId, loLarge, out loLargeId))
    {
      ProcessPair(node, hi, hiLarge, hiLargeId, true, lo, loLarge, loLargeId, false, false);
    }
  }

  // ---------------- 内墙线：完全复用 10.x 决策，但强制“只改 Wallinside” ----------------

  void WallChamferNormalInside(Point3d node, List<AxisEndAtNode> list)
  {
    if (list == null) return;
    int c = list.Count;
    if (c < 2) return;

    for (int i = 0; i < c; i++)
    {
      var axA = list[i];
      var axB = list[(i + 1) % c];

      string sideA = CwSide(axA);
      string sideB = CcwSide(axB);

      Guid objA, objB;
      bool insideA, insideB;

      if (!TryGetWallinsideOrWallObj(axA.AxisId, sideA, out objA, out insideA)) continue;
      if (!TryGetWallinsideOrWallObj(axB.AxisId, sideB, out objB, out insideB)) continue;

      // 至少一侧是内墙线才处理；并且只修改内墙线
      if (!insideA && !insideB) continue;

      ProcessPair(node, axA, sideA, objA, insideA,
                        axB, sideB, objB, insideB,
                        false);
    }
  }

  void WallChamferTargetInside(Point3d node, List<AxisEndAtNode> list, HashSet<Guid> targets)
  {
    if (list == null) return;
    if (targets == null || targets.Count == 0) return;

    int c = list.Count;
    if (c < 2) return;

    for (int i = 0; i < c; i++)
    {
      var axA = list[i];
      var axB = list[(i + 1) % c];

      bool baseA = targets.Contains(axA.AxisId);
      bool baseB = targets.Contains(axB.AxisId);
      if (!baseA && !baseB) continue;

      string sideA = CwSide(axA);
      string sideB = CcwSide(axB);

      Guid objA, objB;
      bool insideA, insideB;

      if (!TryGetWallinsideOrWallObj(axA.AxisId, sideA, out objA, out insideA)) continue;
      if (!TryGetWallinsideOrWallObj(axB.AxisId, sideB, out objB, out insideB)) continue;

      bool modA = baseA && insideA;
      bool modB = baseB && insideB;
      if (!modA && !modB) continue;

      ProcessPair(node, axA, sideA, objA, modA,
                        axB, sideB, objB, modB,
                        false);
    }
  }
  void WallChamferTargetShortenInside(Point3d node, List<AxisEndAtNode> list, HashSet<Guid> targets)
  {
    if (list == null) return;
    if (targets == null || targets.Count == 0) return;

    int c = list.Count;
    if (c < 3)
    {
      WallChamferTargetInside(node, list, targets);
      return;
    }

    for (int i = 0; i < c; i++)
    {
      var ax = list[i];
      if (!targets.Contains(ax.AxisId)) continue;

      var prev = list[(i - 1 + c) % c];
      var next = list[(i + 1) % c];

      // 1) 顺时针侧
      string sideT = CwSide(ax);
      Guid objT;
      bool insideT;
      if (TryGetWallinsideOrWallObj(ax.AxisId, sideT, out objT, out insideT) && insideT)
      {
        Guid objH1 = Guid.Empty, objH2 = Guid.Empty;
        bool insideH1, insideH2;

        TryGetWallinsideOrWallObj(next.AxisId, CcwSide(next), out objH1, out insideH1);
        TryGetWallinsideOrWallObj(prev.AxisId, CwSide(prev),  out objH2, out insideH2);

        ApplySelfShortenToObj(node, ax, objT, next, objH1, prev, objH2);
      }

      // 2) 逆时针侧
      sideT = CcwSide(ax);
      if (TryGetWallinsideOrWallObj(ax.AxisId, sideT, out objT, out insideT) && insideT)
      {
        Guid objH1 = Guid.Empty, objH2 = Guid.Empty;
        bool insideH1, insideH2;

        TryGetWallinsideOrWallObj(prev.AxisId, CwSide(prev),  out objH1, out insideH1);
        TryGetWallinsideOrWallObj(next.AxisId, CcwSide(next), out objH2, out insideH2);

        ApplySelfShortenToObj(node, ax, objT, prev, objH1, next, objH2);
      }
    }
  }


  void WallChamferRankInside(Point3d node, AxisEndAtNode ax0, AxisEndAtNode ax1)
  {
    int lv0 = GetAxisLevel(ax0.AxisId);
    int lv1 = GetAxisLevel(ax1.AxisId);

    double a0 = ClockAngle(ax0);
    double a1 = ClockAngle(ax1);

    double d01 = a1 - a0;
    if (d01 < 0) d01 += 2.0 * Math.PI;
    double d10 = a0 - a1;
    if (d10 < 0) d10 += 2.0 * Math.PI;

    double dSmall = Math.Min(d01, d10);

    const double AngTol = 1e-6;
    if (dSmall < AngTol) return;
    if (Math.Abs(dSmall - Math.PI) < AngTol) return;

    bool smallIs01 = (d01 <= d10);

    string ax0Small, ax0Large, ax1Small, ax1Large;
    if (smallIs01)
    {
      ax0Small = CwSide(ax0);
      ax0Large = CcwSide(ax0);

      ax1Small = CcwSide(ax1);
      ax1Large = CwSide(ax1);
    }
    else
    {
      ax1Small = CwSide(ax1);
      ax1Large = CcwSide(ax1);

      ax0Small = CcwSide(ax0);
      ax0Large = CwSide(ax0);
    }

    AxisEndAtNode hi = (lv0 >= lv1) ? ax0 : ax1;
    AxisEndAtNode lo = (hi.AxisId == ax0.AxisId) ? ax1 : ax0;

    string hiSmall = (hi.AxisId == ax0.AxisId) ? ax0Small : ax1Small;
    string hiLarge = (hi.AxisId == ax0.AxisId) ? ax0Large : ax1Large;

    string loSmall = (lo.AxisId == ax0.AxisId) ? ax0Small : ax1Small;
    string loLarge = (lo.AxisId == ax0.AxisId) ? ax0Large : ax1Large;

    void ProcessRankPair(AxisEndAtNode A, string sideA, bool baseModA,
                         AxisEndAtNode B, string sideB, bool baseModB)
    {
      Guid objA, objB;
      bool insideA, insideB;

      if (!TryGetWallinsideOrWallObj(A.AxisId, sideA, out objA, out insideA)) return;
      if (!TryGetWallinsideOrWallObj(B.AxisId, sideB, out objB, out insideB)) return;

      bool modA = baseModA && insideA;
      bool modB = baseModB && insideB;
      if (!modA && !modB) return;

      ProcessPair(node, A, sideA, objA, modA,
                        B, sideB, objB, modB,
                        false);
    }

    // 1) 小角侧：高级(小) + 低级(大)（原本两条都改）→ inside 版本：只改内墙线一侧/两侧
    ProcessRankPair(hi, hiSmall, true, lo, loLarge, true);

    // 2) 小角侧：低级(小) 自身导角（只改低级这条）→ inside 版本：仅当低级侧存在内墙线时才改
    ProcessRankPair(lo, loSmall, true, hi, hiSmall, false);

    // 3) 大角侧：高级(大) 自身导角（只改高级这条）→ inside 版本：仅当高级侧存在内墙线时才改
    ProcessRankPair(hi, hiLarge, true, lo, loLarge, false);
  }

  // ---------------- 统一调度：10.x 决策对 Wall + Wallinside 同步执行 ----------------

  void RunWallChamferAtNode(Point3d node, List<AxisEndAtNode> endsAtNode)
  {
    if (endsAtNode == null) return;
    int count = endsAtNode.Count;
    if (count < 2) return;

    var kindCount = new Dictionary<WallKind, int>();
    var kindAxis  = new Dictionary<WallKind, List<Guid>>();

    for (int i = 0; i < endsAtNode.Count; i++)
    {
      var ax = endsAtNode[i];
      WallKind k = GetAxisKind(ax.AxisId);

      int c;
      if (!kindCount.TryGetValue(k, out c)) c = 0;
      kindCount[k] = c + 1;

      List<Guid> ids;
      if (!kindAxis.TryGetValue(k, out ids))
      {
        ids = new List<Guid>();
        kindAxis[k] = ids;
      }
      ids.Add(ax.AxisId);
    }

    // 10.1：type 全部相同 → 普通导角
    if (kindCount.Count <= 1)
    {
      WallChamferNormal(node, endsAtNode);
      WallChamferNormalInside(node, endsAtNode);
      return;
    }

    // 10.2：只有两根轴线且 type 不同 → 等级导角
    if (count == 2)
    {
      WallChamferRank(node, endsAtNode[0], endsAtNode[1]);
      WallChamferRankInside(node, endsAtNode[0], endsAtNode[1]);
      return;
    }

    // 统计“某个 type 数量 > 1”的种类数
    int multiKindCount = 0;
    WallKind onlyMultiKind = WallKind.Block;
    foreach (var kv in kindCount)
    {
      if (kv.Value > 1)
      {
        multiKindCount++;
        onlyMultiKind = kv.Key;
      }
    }

    bool allUnique = (kindCount.Count == count);

    // 10.3：>2 根且没有相同 type → 先对最高两根做等级导角，再依次对剩余做“自身导角”
    if (allUnique)
    {
      var byLevel = new List<AxisEndAtNode>(endsAtNode);
      byLevel.Sort((a, b) => GetAxisLevel(b.AxisId).CompareTo(GetAxisLevel(a.AxisId)));
      if (byLevel.Count < 2) return;

      var A = byLevel[0];
      var B = byLevel[1];

      WallChamferRank(node, A, B);
      WallChamferRankInside(node, A, B);

      var done = new HashSet<Guid>();
      done.Add(A.AxisId);
      done.Add(B.AxisId);

      for (int i = 2; i < byLevel.Count; i++)
      {
        var X = byLevel[i];

        var subsetIds = new HashSet<Guid>(done);
        subsetIds.Add(X.AxisId);

        var subsetEnds = BuildSubsetEnds(endsAtNode, subsetIds);

        var targets = new HashSet<Guid>();
        targets.Add(X.AxisId);

        WallChamferTargetShorten(node, subsetEnds, targets);
        WallChamferTargetShortenInside(node, subsetEnds, targets);

        done.Add(X.AxisId);
      }

      return;
    }

    // 10.4：仅有一个 type 的数量 >1，其余 type 最多 1 根
    if (multiKindCount == 1)
    {
      List<Guid> aIds;
      if (!kindAxis.TryGetValue(onlyMultiKind, out aIds) || aIds == null || aIds.Count < 2)
      {
        WallChamferNormal(node, endsAtNode);
        WallChamferNormalInside(node, endsAtNode);
        return;
      }

      var done = new HashSet<Guid>(aIds);

      // A：先只对同 type 的 A 组做普通导角（忽略其它轴线）
      var aEnds = BuildSubsetEnds(endsAtNode, done);
      WallChamferNormal(node, aEnds);
      WallChamferNormalInside(node, aEnds);

      // 依次对剩余轴线按等级从高到低做“自身导角”
      var remain = new List<AxisEndAtNode>();
      for (int i = 0; i < endsAtNode.Count; i++)
      {
        var ax = endsAtNode[i];
        if (!done.Contains(ax.AxisId))
          remain.Add(ax);
      }
      remain.Sort((a, b) => GetAxisLevel(b.AxisId).CompareTo(GetAxisLevel(a.AxisId)));

      for (int i = 0; i < remain.Count; i++)
      {
        var X = remain[i];

        var subsetIds = new HashSet<Guid>(done);
        subsetIds.Add(X.AxisId);

        var subsetEnds = BuildSubsetEnds(endsAtNode, subsetIds);

        var targets = new HashSet<Guid>();
        targets.Add(X.AxisId);

        WallChamferTargetShorten(node, subsetEnds, targets);
        WallChamferTargetShortenInside(node, subsetEnds, targets);

        done.Add(X.AxisId);
      }

      return;
    }

    // 10.5：不止一个 type 的数量 >1
    // 先取“数量>1 的 type 里级别最高”的作为 A 组
    WallKind bestKind = WallKind.Block;
    int bestLv = int.MinValue;
    foreach (var kv in kindCount)
    {
      if (kv.Value <= 1) continue;
      int lv = GetWallKindLevel(kv.Key);
      if (lv > bestLv)
      {
        bestLv = lv;
        bestKind = kv.Key;
      }
    }

    List<Guid> bestIds;
    if (!kindAxis.TryGetValue(bestKind, out bestIds) || bestIds == null || bestIds.Count < 2)
    {
      WallChamferNormal(node, endsAtNode);
      WallChamferNormalInside(node, endsAtNode);
      return;
    }

    var doneSet = new HashSet<Guid>(bestIds);

    // A：先只对 A 组做普通导角（忽略其它轴线）
    var bestEnds = BuildSubsetEnds(endsAtNode, doneSet);
    WallChamferNormal(node, bestEnds);
    WallChamferNormalInside(node, bestEnds);

    // 其余 type 按等级从高到低，依次加入并只处理当前 type
    var kinds = new List<WallKind>();
    foreach (var kv in kindCount)
    {
      if (kv.Key == bestKind) continue;
      kinds.Add(kv.Key);
    }
    kinds.Sort((a, b) => GetWallKindLevel(b).CompareTo(GetWallKindLevel(a)));

    for (int ki = 0; ki < kinds.Count; ki++)
    {
      WallKind k = kinds[ki];
      List<Guid> ids;
      if (!kindAxis.TryGetValue(k, out ids) || ids == null || ids.Count == 0)
        continue;

      var cur = new HashSet<Guid>(ids);

      var subsetIds = new HashSet<Guid>(doneSet);
      foreach (var id in cur) subsetIds.Add(id);

      var subsetEnds = BuildSubsetEnds(endsAtNode, subsetIds);

      WallChamferTargetShorten(node, subsetEnds, cur);
      WallChamferTargetShortenInside(node, subsetEnds, cur);

      foreach (var id in cur) doneSet.Add(id);
    }
  }

  // ---------- 6) 对每个节点：排序轴线 → 相邻构对 ----------
  int nodeCount = 0;

  foreach (var nk in nodeKeys)
  {
    List<AxisEndAtNode> ends;
    if (!endpointMap.TryGetValue(nk, out ends) || ends == null || ends.Count < 2)
      continue;

    // 节点真实点用“平均”更稳（避免量化误差）
    Point3d node = Point3d.Origin;
    int n = 0;
    for (int i = 0; i < ends.Count; i++)
    {
      var ax = ends[i].AxisCurve;
      if (ax == null) continue;
      Point3d p = ends[i].IsStart ? ax.PointAtStart : ax.PointAtEnd;
      node = new Point3d(node.X + p.X, node.Y + p.Y, node.Z + p.Z);
      n++;
    }
    if (n == 0) continue;
    node = new Point3d(node.X / n, node.Y / n, node.Z / n);

    // 按“12点起顺时针”排序
    ends.Sort((a, b) =>
    {
      double aa = Math.Atan2(a.OutDir.X, a.OutDir.Y);
      double bb = Math.Atan2(b.OutDir.X, b.OutDir.Y);
      if (aa < 0) aa += 2.0 * Math.PI;
      if (bb < 0) bb += 2.0 * Math.PI;
      return aa.CompareTo(bb);
    });

    int count = ends.Count;
    if (count < 2) continue;

    nodeCount++;

    // ---------- 6.1) 墙线导角：type/等级特殊策略（10.x） ----------
    RunWallChamferAtNode(node, ends);

    // ---------- 6.2) 保温线导角：保持 1.0.8 原逻辑（完全不受 type 影响） ----------
    for (int i = 0; i < count; i++)
    {
      var axA = ends[i];
      var axB = ends[(i + 1) % count];

      string sideA = (axA.VectorDir == 1) ? "right" : "left"; // A 的顺时针侧
      string sideB = (axB.VectorDir == 1) ? "left" : "right"; // B 的逆时针侧

      // 仍然需要 wallId：当一侧没有保温线时，保温线自身导角要用对侧墙线做“帮忙线”
      Guid wallAId = Guid.Empty;
      Guid wallBId = Guid.Empty;
      wallMap.TryGetValue(Key(axA.AxisId, sideA), out wallAId);
      wallMap.TryGetValue(Key(axB.AxisId, sideB), out wallBId);

      Guid insAId = Guid.Empty;
      Guid insBId = Guid.Empty;
      insulMap.TryGetValue(Key(axA.AxisId, sideA), out insAId);
      insulMap.TryGetValue(Key(axB.AxisId, sideB), out insBId);

      if (insAId != Guid.Empty && insBId != Guid.Empty)
      {
        ProcessPair(node, axA, sideA, insAId, true, axB, sideB, insBId, true, true);
      }
      else if (insAId != Guid.Empty && wallBId != Guid.Empty)
      {
        ProcessPair(node, axA, sideA, insAId, true, axB, sideB, wallBId, false, true);
      }
      else if (insBId != Guid.Empty && wallAId != Guid.Empty)
      {
        ProcessPair(node, axA, sideA, wallAId, false, axB, sideB, insBId, true, true);
      }
    }
  }

  // ---------- 7) 最后统一重建直线墙线 / 直线保温线 ----------
  void RebuildLines(Dictionary<Guid, Dictionary<CurveEnd, Point3d>> hitMap)
  {
    foreach (var kv in hitMap)
    {
      Guid objId = kv.Key;
      var ends = kv.Value;
      if (ends == null || ends.Count == 0) continue;

      var co = doc.Objects.FindId(objId) as CurveObject;
      var crv0 = co?.Geometry as Curve;
      if (crv0 == null) continue;

      Point3d p0 = crv0.PointAtStart;
      Point3d p1 = crv0.PointAtEnd;

      Point3d np0 = p0;
      Point3d np1 = p1;

      Point3d ps;
      if (ends.TryGetValue(CurveEnd.Start, out ps)) np0 = ps;

      Point3d pe;
      if (ends.TryGetValue(CurveEnd.End, out pe)) np1 = pe;

      if (np0.DistanceToSquared(np1) < tolInt * tolInt)
        continue;

      var newLine = new LineCurve(np0, np1);

      EnsureUnlockedByObjId(objId);
      if (doc.Objects.Replace(objId, newLine))
        SyncObjStartEndName(objId);
    }
  }

  RebuildLines(lineHitWall);
  RebuildLines(lineHitInsul);

  RestoreLayerLocks();

  RhinoApp.WriteLine($"[Chamfer] 节点={nodeCount}，墙线重建={lineHitWall.Count}，保温重建={lineHitInsul.Count}。");
}



AxisCurveKind GetAxisKindAndNormalize(Curve c, double tol, out Curve normalized)
{
  normalized = c;
  if (c == null || !c.IsValid) return AxisCurveKind.Spline;

  // 1) 直线：哪怕是 NurbsCurve，只要几何上线性，就强制转回 LineCurve
  if (c.IsLinear(tol))
  {
    normalized = new LineCurve(c.PointAtStart, c.PointAtEnd);
    return AxisCurveKind.Line;
  }

  // 2) 圆弧：很多“用 Nurbs 表达的圆弧”也能抓出来
  Arc arc;
  if (c.TryGetArc(out arc))
  {
    normalized = new ArcCurve(arc);
    return AxisCurveKind.Arc;
  }

  // 3) 其它都当 spline
  normalized = c.DuplicateCurve();
  return AxisCurveKind.Spline;
}


//以下全是开关封口的辅助函数

// ============================================================
// 开关封口：点击轴线端头，自动在 Start/End 上切换 cap
// ============================================================
void ToggleWallCapByPick(RhinoDoc doc)
{
  if (doc == null) return;

  const double SearchRadius = 1000.0; // 你说半径 1000 粗筛墙线/封口线
  double tol = doc.ModelAbsoluteTolerance;

  // 1) 让用户点“轴线端头”
  var go = new GetObject();
  go.SetCommandPrompt("请点击需要开关封口的轴线端头");
  go.GeometryFilter  = ObjectType.Curve;
  go.SubObjectSelect = false;
  go.EnablePreSelect(true, true);
  go.EnablePostSelect(true);
  go.Get();

  if (go.CommandResult() != Rhino.Commands.Result.Success)
    return;

  var objRef = go.Object(0);
  if (objRef == null) return;

  var axisObj = objRef.Object() as RhinoObject;
  var axisCrv = objRef.Curve();
  if (axisObj == null || axisCrv == null || !axisCrv.IsValid) return;

  // 2) 必须是轴线层对象（PB-Wall-Axis），否则拒绝
  int axisLayerIndex = axisObj.Attributes.LayerIndex;
  var axisLayer = (axisLayerIndex >= 0) ? doc.Layers[axisLayerIndex] : null;
  if (axisLayer == null || !axisLayer.Name.Equals("PB-Wall-Axis", StringComparison.OrdinalIgnoreCase))
  {
    RhinoApp.WriteLine("[CapToggle] 请选择 PB-Wall-Axis 图层中的轴线。");
    return;
  }

  // 3) 推出 parentId（严格限制在同一父级图层树内）
  Guid parentId = axisLayer.ParentLayerId;
  if (parentId == Guid.Empty) parentId = axisLayer.Id; // ★兜底：顶层分区用自己当 parent
  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;

  if (parentId == Guid.Empty) parentId = axisLayer.Id;

  if (AbortIfAnyAxisOverlap(doc, parentId, null, tol))
  return;

  // 4) 点选位置 → 判定更接近 Start 还是 End
  Point3d pickPt = objRef.SelectionPoint();
  Point3d st = axisCrv.PointAtStart;
  Point3d ed = axisCrv.PointAtEnd;

  bool isStart = (pickPt.DistanceTo(st) <= pickPt.DistanceTo(ed));
  Point3d axisEndPt = isStart ? st : ed;
  string  atTag = isStart ? "st" : "ed";  // 用于 wallcap|...|at=st/ed

  // 5) 获取 axisId（优先从 Name 的 AxisId= 读；读不到就用对象自身 Id）
  Guid axisId;
  if (!TryGetAxisIdFromName(axisObj.Attributes.Name ?? "", out axisId))
    axisId = axisObj.Id;

  // 6) 读取 capston/capedon
  string axisName = axisObj.Attributes.Name ?? "";
  int capVal = GetTag01(axisName, isStart ? "capston" : "capedon", 0);

  // 7) 需要解锁的层：轴线层 + 相关墙线层（cap线也会落在墙线层）
  var layersToUnlock = new HashSet<int>();
  var lockBackup = new Dictionary<int, bool>();

  void AddUnlockLayer(int li)
  {
    if (li < 0) return;
    var lay = doc.Layers[li];
    if (lay == null) return;
    if (!layersToUnlock.Contains(li) && lay.IsLocked)
      layersToUnlock.Add(li);
  }

  AddUnlockLayer(axisLayerIndex);

  foreach (int li in layersToUnlock)
  {
    var lay = doc.Layers[li];
    lockBackup[li] = lay.IsLocked;
    lay.IsLocked = false;
    doc.Layers.Modify(lay, li, true);
  }

  try
  {
    if (capVal == 1)
    {
      // =========================
      // 3.4：有封口 → 找到并删除 cap 线，同时把 cap 标签改成 0
      // =========================
      int deleted = DeleteWallCapsAtEnd(doc, parentId, axisId, atTag, axisEndPt, SearchRadius, tol);

      // 无论删没删到，都把标签改成 0（防“标签=1 但线丢了”的脏状态）
      axisName = SetOrReplaceTag01(axisName, isStart ? "capston" : "capedon", 0);
      ModifyObjectName(doc, axisObj, axisName);

      RhinoApp.WriteLine("[CapToggle] 已关闭封口：删除 cap {0} 条", deleted);
    }
    else
    {
      // =========================
      // 3.5：无封口 → 找轴线对应的左右墙线 → 取端点 → 生成 cap 线，同时把 cap 标签改成 1
      // =========================
      if (!TryFindLeftRightWallsForAxis(doc, parentId, axisId, axisEndPt, SearchRadius, tol,
            out RhinoObject wallLObj, out RhinoObject wallRObj))
      {
        RhinoApp.WriteLine("[CapToggle] 未找到该轴线对应的左右墙线（Wall|AxisId=...|Side=...）。");
        return;
      }

      var wallLCrv = wallLObj.Geometry as Curve;
      var wallRCrv = wallRObj.Geometry as Curve;
      if (wallLCrv == null || wallRCrv == null || !wallLCrv.IsValid || !wallRCrv.IsValid)
      {
        RhinoApp.WriteLine("[CapToggle] 墙线几何无效，无法封口。");
        return;
      }

      // ★ 1.3.2：封口端点严格对应轴线端点：轴线 Start → 墙线 Start；轴线 End → 墙线 End
      Point3d pL = isStart ? wallLCrv.PointAtStart : wallLCrv.PointAtEnd;
      Point3d pR = isStart ? wallRCrv.PointAtStart : wallRCrv.PointAtEnd;


      // 先防重复：把这个端头已存在的 cap 全删掉（即使 capVal=0，也可能残留）
      DeleteWallCapsAtEnd(doc, parentId, axisId, atTag, axisEndPt, SearchRadius, tol);

      // cap 落在墙线同层（用左墙线层）
      int capLayerIndex = wallLObj.Attributes.LayerIndex;
      AddUnlockLayer(capLayerIndex);
      // 如果刚才新增了解锁层，立即解锁（只针对本次新增）
      if (doc.Layers[capLayerIndex] != null && doc.Layers[capLayerIndex].IsLocked)
      {
        lockBackup[capLayerIndex] = true;
        var lay = doc.Layers[capLayerIndex];
        lay.IsLocked = false;
        doc.Layers.Modify(lay, capLayerIndex, true);
      }

      var attr = wallLObj.Attributes.Duplicate();
      attr.LayerIndex = capLayerIndex;
      attr.Name =
        $"wallcap|axisid={axisId}|at={atTag}" +
        $"|start={FormatPoint(pL)}|end={FormatPoint(pR)}";

      doc.Objects.AddCurve(new LineCurve(pL, pR), attr);

      axisName = SetOrReplaceTag01(axisName, isStart ? "capston" : "capedon", 1);
      ModifyObjectName(doc, axisObj, axisName);

      RhinoApp.WriteLine("[CapToggle] 已开启封口：生成 1 条 cap");
    }
  }
  finally
  {
    // 还原锁
    foreach (var kv in lockBackup)
    {
      var lay = doc.Layers[kv.Key];
      if (lay != null)
      {
        lay.IsLocked = kv.Value;
        doc.Layers.Modify(lay, kv.Key, true);
      }
    }
    doc.Views.Redraw();
  }
}

// ============================================================
// 辅助：删除某个轴线某个端头附近的 cap（wallcap|axisid=...|at=st/ed）
// ============================================================
int DeleteWallCapsAtEnd(
  RhinoDoc doc,
  Guid parentId,
  Guid axisId,
  string atTag,            // "st" / "ed"
  Point3d axisEndPt,
  double radius,
  double tol)
{
  int deleted = 0;

  var oes = NewEnumeratorAllCurves();
  foreach (var ro in doc.Objects.GetObjectList(oes))
  {
    if (ro == null) continue;
    if (!(ro.Geometry is Curve c) || !c.IsValid) continue;

    if (!IsLayerUnderParent(doc, ro.Attributes.LayerIndex, parentId))
      continue;

    // 先粗筛：离端头太远的别看
    var bb = c.GetBoundingBox(true);
    if (!bb.IsValid) continue;

    // bb 内部距离 = 0；否则用最近点距离
    double d = bb.Contains(axisEndPt) ? 0.0 : bb.ClosestPoint(axisEndPt).DistanceTo(axisEndPt);
    if (d > radius) continue;

    string nm = ro.Attributes.Name ?? "";
    if (!nm.StartsWith("wallcap", StringComparison.OrdinalIgnoreCase) &&
        !nm.StartsWith("Wallcap", StringComparison.OrdinalIgnoreCase))
      continue;

    Guid aid;
    if (!TryGetAxisIdFromName(nm, out aid)) continue;
    if (aid != axisId) continue;

    // at=st/ed 不强制必须存在，但如果存在就要求匹配
    string at = GetTagString(nm, "at", "");
    if (!string.IsNullOrEmpty(at) && !at.Equals(atTag, StringComparison.OrdinalIgnoreCase))
      continue;

    // 再几何确认：cap 的任一端点必须贴近该轴线端头
    var p0 = c.PointAtStart;
    var p1 = c.PointAtEnd;
    if (Math.Min(p0.DistanceTo(axisEndPt), p1.DistanceTo(axisEndPt)) > radius)
      continue;

    doc.Objects.Delete(ro, true);
    deleted++;
  }

  return deleted;
}

// ============================================================
// 辅助：找某轴线的左右墙线（Wall|AxisId=...|Side=Left/Right）
// - 限制在 parentId 图层树内
// - 只扫描 PB-Wall / PB-Wall-Glas / PB-Wall-Stud
// - 先用 bbox 距离端头半径粗筛
// ============================================================
bool TryFindLeftRightWallsForAxis(
  RhinoDoc doc,
  Guid parentId,
  Guid axisId,
  Point3d axisEndPt,
  double radius,
  double tol,
  out RhinoObject wallLObj,
  out RhinoObject wallRObj)
{
  wallLObj = null;
  wallRObj = null;

  var oes = NewEnumeratorAllCurves();
  foreach (var ro in doc.Objects.GetObjectList(oes))
  {
    if (ro == null) continue;
    if (!(ro.Geometry is Curve c) || !c.IsValid) continue;

    int li = ro.Attributes.LayerIndex;
    if (!IsLayerUnderParent(doc, li, parentId)) continue;

    var lay = (li >= 0) ? doc.Layers[li] : null;
    if (lay == null) continue;

    string ln = lay.Name ?? "";
    bool isWallLayer =
         ln.Equals("PB-Wall", StringComparison.OrdinalIgnoreCase)
      || ln.Equals("PB-Wall-Glas", StringComparison.OrdinalIgnoreCase)
      || ln.Equals("PB-Wall-Glass", StringComparison.OrdinalIgnoreCase)
      || ln.Equals("PB-Wall-Stud", StringComparison.OrdinalIgnoreCase);

    if (!isWallLayer) continue;

    // 粗筛：离端头太远就跳过
    var bb = c.GetBoundingBox(true);
    if (!bb.IsValid) continue;

    // bb 内部距离 = 0；否则用最近点距离
    double d = bb.Contains(axisEndPt) ? 0.0 : bb.ClosestPoint(axisEndPt).DistanceTo(axisEndPt);
    if (d > radius) continue;

    string nm = ro.Attributes.Name ?? "";
    if (string.IsNullOrEmpty(nm)) continue;

    // 只要墙线：必须以 "Wall|" 或者就是 "Wall"（兼容）
    if (!(nm.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase) ||
          nm.Equals("Wall", StringComparison.OrdinalIgnoreCase)))
      continue;

    // 解析 AxisId
    Guid aid;
    if (!TryGetAxisIdFromName(nm, out aid)) continue;
    if (aid != axisId) continue;

    // 解析 Side
    bool isL, isR;
    if (!TryGetSideFromName(nm, out isL, out isR)) continue;

    if (isL && wallLObj == null) wallLObj = ro;
    if (isR && wallRObj == null) wallRObj = ro;

    if (wallLObj != null && wallRObj != null)
      return true;
  }

  return wallLObj != null && wallRObj != null;
}

// ============================================================
// 辅助：墙线端点配对（取更靠近 axisEndPt 的端点）
// ============================================================
bool TryGetWallEndPointNear(Curve wall, Point3d axisEndPt, double radius, out Point3d wallEndPt)
{
  wallEndPt = Point3d.Unset;
  if (wall == null || !wall.IsValid) return false;

  var a = wall.PointAtStart;
  var b = wall.PointAtEnd;

  double da = a.DistanceTo(axisEndPt);
  double db = b.DistanceTo(axisEndPt);

  wallEndPt = (da <= db) ? a : b;
  return Math.Min(da, db) <= radius;
}

// ============================================================
// 工具：从 Name 中解析 AxisId=GUID（你墙线/轴线都是这个格式）
// ============================================================
bool TryGetAxisIdFromName(string name, out Guid axisId)
{
  axisId = Guid.Empty;
  if (string.IsNullOrEmpty(name)) return false;

  var m = Regex.Match(
    name,
    @"(?i)\baxisid\b\s*=\s*([0-9a-f]{8}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{4}\-[0-9a-f]{12})\b");
  return m.Success && Guid.TryParse(m.Groups[1].Value, out axisId);
}

// ============================================================
// 工具：解析 Side=Left/Right
// ============================================================
bool TryGetSideFromName(string name, out bool isLeft, out bool isRight)
{
  isLeft = false; isRight = false;
  if (string.IsNullOrEmpty(name)) return false;

  var m = Regex.Match(name, @"(?i)\bside\b\s*=\s*(left|right|l|r)\b");
  if (!m.Success) return false;

  string v = m.Groups[1].Value.ToLowerInvariant();
  isLeft  = (v == "left"  || v == "l");
  isRight = (v == "right" || v == "r");
  return isLeft || isRight;
}

// ============================================================
// 工具：读取 |key=0/1
// ============================================================
int GetTag01(string name, string keyLower, int defaultVal)
{
  if (string.IsNullOrEmpty(name)) return defaultVal;
  var m = Regex.Match(name, @"(?i)\|" + Regex.Escape(keyLower) + @"\s*=\s*([01])\b");
  if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
  return defaultVal;
}

string SetOrReplaceTag01(string name, string keyLower, int v01)
{
  if (string.IsNullOrEmpty(name)) name = "Wallaxis";
  string pat = @"(?i)(\|" + Regex.Escape(keyLower) + @"\s*=\s*[01])";
  string rep = "|" + keyLower + "=" + (v01 != 0 ? 1 : 0);
  if (Regex.IsMatch(name, pat)) return Regex.Replace(name, pat, rep);
  return name + rep;
}

// 读取 |key=value（字符串）
string GetTagString(string name, string keyLower, string defaultVal)
{
  if (string.IsNullOrEmpty(name)) return defaultVal;
  var m = Regex.Match(name, @"(?i)\|" + Regex.Escape(keyLower) + @"\s*=\s*([^|]+)");
  if (m.Success) return m.Groups[1].Value.Trim();
  return defaultVal;
}

// ============================================================
// 工具：修改对象 Name（保留其它 Attributes）
// ============================================================
void ModifyObjectName(RhinoDoc doc, RhinoObject obj, string newName)
{
  if (doc == null || obj == null) return;
  var a = obj.Attributes.Duplicate();
  a.Name = newName ?? "";
  doc.Objects.ModifyAttributes(obj, a, true);
}

// ============================================================
// 工具：判断 layerIndex 是否在 parentId 的图层树之下
// ============================================================
bool IsLayerUnderParent(RhinoDoc doc, int layerIndex, Guid parentId)
{
  if (doc == null) return false;
  if (parentId == Guid.Empty) return true;
  if (layerIndex < 0) return false;

  var lay = doc.Layers[layerIndex];
  while (lay != null)
  {
    if (lay.Id == parentId) return true;
    if (lay.ParentLayerId == Guid.Empty) break;
    lay = doc.Layers.FindId(lay.ParentLayerId);
  }
  return false;
}

// 工具：枚举（包含锁定/隐藏/引用）所有曲线对象
ObjectEnumeratorSettings NewEnumeratorAllCurves()
{
  void TrySetEnum(ObjectEnumeratorSettings s, string prop, bool v)
  {
    var pi = s.GetType().GetProperty(prop);
    if (pi != null && pi.CanWrite && pi.PropertyType == typeof(bool))
      pi.SetValue(s, v, null);
  }

  var oes = new ObjectEnumeratorSettings
  {
    ObjectTypeFilter = ObjectType.Curve,
    IncludeLights    = false,
    IncludeGrips     = false
  };
  TrySetEnum(oes, "ActiveObjects",    true);
  TrySetEnum(oes, "NormalObjects",    true);
  TrySetEnum(oes, "LockedObjects",    true);
  TrySetEnum(oes, "HiddenObjects",    true);
  TrySetEnum(oes, "ReferenceObjects", true);
  return oes;
}

//以上全是开关封口的辅助函数

//以下是检测重叠线的辅助函数⬇⬇⬇⬇⬇⬇
int EnsureOverlapMarkLayerIndex(RhinoDoc doc, Guid parentId)
{
  if (doc == null) return -1;
  if (parentId == Guid.Empty) return -1;

  int li = FindSiblingLayerIndex(doc, parentId, "Px-Mark-Overlap");
  if (li >= 0)
  {
    var ly = doc.Layers[li];
    if (ly != null)
    {
      bool dirty = false;
      if (ly.Color != SDColor.Red) { ly.Color = SDColor.Red; dirty = true; }
      if (ly.IsLocked) { ly.IsLocked = false; dirty = true; }
      if (dirty) doc.Layers.Modify(ly, li, true);
    }
    return li;
  }

  var layer = new Layer();
  layer.Name = "Px-Mark-Overlap";
  layer.ParentLayerId = parentId;
  layer.Color = SDColor.Red;
  layer.IsVisible = true;
  layer.IsLocked = false;
  return doc.Layers.Add(layer);
}

void ClearOverlapMarksInLayer(RhinoDoc doc, int layerIndex)
{
  if (doc == null) return;
  if (layerIndex < 0 || layerIndex >= doc.Layers.Count) return;

  var layer = doc.Layers[layerIndex];
  if (layer == null) return;

  bool wasLocked = layer.IsLocked;
  if (wasLocked)
  {
    layer.IsLocked = false;
    doc.Layers.Modify(layer, layerIndex, true);
  }

  var objs = doc.Objects.FindByLayer(layer);
  if (objs != null)
  {
    foreach (var o in objs)
      if (o != null) doc.Objects.Delete(o, true);
  }

  if (wasLocked)
  {
    layer.IsLocked = true;
    doc.Layers.Modify(layer, layerIndex, true);
  }
}

bool AbortIfAnyAxisOverlap(RhinoDoc doc, Guid parentId, IEnumerable<Curve> extraCurves, double tol, HashSet<Guid> ignoreAxisIds = null)
{
  if (doc == null) return false;
  if (parentId == Guid.Empty) return false;

  double overlapTol = Math.Max(tol, tol * 10.0);
  double mmScale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
  double r = 200.0 * mmScale;

  // 1) 收集本分区 PB-Wall-Axis 下所有 Wallaxis 曲线
  var curves = new List<Curve>();
  var bbs    = new List<BoundingBox>();

  int axisLi = FindSiblingLayerIndex(doc, parentId, "PB-Wall-Axis");
  if (axisLi >= 0 && axisLi < doc.Layers.Count)
  {
    var axisLayer = doc.Layers[axisLi];
    if (axisLayer != null)
    {
      var axisObjs = doc.Objects.FindByLayer(axisLayer) ?? Array.Empty<RhinoObject>();
      foreach (var ro in axisObjs)
      {
        var co = ro as CurveObject;
        if (co == null) continue;

        // ★ 新增：忽略指定轴线（用于“源 Wallaxis 自重叠误判”）
        if (ignoreAxisIds != null && ignoreAxisIds.Contains(co.Id))
        continue;

        string n = co.Attributes.Name ?? "";
        if (!n.StartsWith("Wallaxis", StringComparison.OrdinalIgnoreCase))
          continue;

        var c0 = co.Geometry as Curve;
        if (c0 == null || !c0.IsValid) continue;

        var c = c0.DuplicateCurve();
        if (c == null || !c.IsValid) continue;

        curves.Add(c);
        var bb = c.GetBoundingBox(true);
        bb.Inflate(tol);
        bbs.Add(bb);
      }
    }
  }

  // 2) extraCurves（本轮新画/待提交的轴线）也纳入检测
  var extra = new List<Curve>();
  if (extraCurves != null)
  {
    foreach (var c0 in extraCurves)
    {
      if (c0 == null || !c0.IsValid) continue;
      var c = c0.DuplicateCurve();
      if (c == null || !c.IsValid) continue;
      extra.Add(c);
    }
  }

  if (curves.Count + extra.Count < 2) return false;

  // 去重点
  var markPts = new Dictionary<string, Point3d>();
  double keySize = Math.Max(mmScale, tol * 10.0);

  void AddMarkPt(Point3d p)
  {
    string key =
      ((int)Math.Round(p.X / keySize)) + "|" +
      ((int)Math.Round(p.Y / keySize)) + "|" +
      ((int)Math.Round(p.Z / keySize));
    if (!markPts.ContainsKey(key))
      markPts[key] = p;
  }

  bool BBoxIntersects(BoundingBox a, BoundingBox b)
 {
  if (!a.IsValid || !b.IsValid) return false;

  return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
           a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
           a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
 }


  // 3) 用 RTree 做粗筛
  var tree = new RTree();
  for (int i = 0; i < curves.Count; i++)
    tree.Insert(bbs[i], i);

  // 3.1 existing-existing
  for (int i = 0; i < curves.Count; i++)
  {
    var candidates = new List<int>();
    tree.Search(bbs[i], (s, a) => { if (a.Id > i) candidates.Add(a.Id); });

    foreach (int j in candidates)
    {
      if (!BBoxIntersects(bbs[i], bbs[j])) continue;


      var xs = Rhino.Geometry.Intersect.Intersection.CurveCurve(curves[i], curves[j], tol, overlapTol);
      if (xs == null || xs.Count == 0) continue;

      foreach (var ev in xs)
      {
        if (!ev.IsOverlap) continue;

        var ia = ev.OverlapA;
        Point3d pt;
        if (ia.IsValid && Math.Abs(ia.T1 - ia.T0) > tol)
          pt = curves[i].PointAt(0.5 * (ia.T0 + ia.T1));
        else
          pt = 0.5 * (ev.PointA + ev.PointB);

        AddMarkPt(pt);
      }
    }
  }

  // 3.2 extra-existing
  foreach (var ec in extra)
  {
    var ebb = ec.GetBoundingBox(true);
    ebb.Inflate(tol);

    var candidates = new List<int>();
    tree.Search(ebb, (s, a) => candidates.Add(a.Id));

    foreach (int j in candidates)
    {
      if (!BBoxIntersects(ebb, bbs[j])) continue;


      var xs = Rhino.Geometry.Intersect.Intersection.CurveCurve(ec, curves[j], tol, overlapTol);
      if (xs == null || xs.Count == 0) continue;

      foreach (var ev in xs)
      {
        if (!ev.IsOverlap) continue;

        var ia = ev.OverlapA;
        Point3d pt;
        if (ia.IsValid && Math.Abs(ia.T1 - ia.T0) > tol)
          pt = ec.PointAt(0.5 * (ia.T0 + ia.T1));
        else
          pt = 0.5 * (ev.PointA + ev.PointB);

        AddMarkPt(pt);
      }
    }
  }

  // 3.3 extra-extra（一般很少，直接两两比）
  for (int i = 0; i < extra.Count; i++)
  for (int j = i + 1; j < extra.Count; j++)
  {
    var bb1 = extra[i].GetBoundingBox(true); bb1.Inflate(tol);
    var bb2 = extra[j].GetBoundingBox(true); bb2.Inflate(tol);
    if (!BBoxIntersects(bb1, bb2)) continue;


    var xs = Rhino.Geometry.Intersect.Intersection.CurveCurve(extra[i], extra[j], tol, overlapTol);
    if (xs == null || xs.Count == 0) continue;

    foreach (var ev in xs)
    {
      if (!ev.IsOverlap) continue;

      var ia = ev.OverlapA;
      Point3d pt;
      if (ia.IsValid && Math.Abs(ia.T1 - ia.T0) > tol)
        pt = extra[i].PointAt(0.5 * (ia.T0 + ia.T1));
      else
        pt = 0.5 * (ev.PointA + ev.PointB);

      AddMarkPt(pt);
    }
  }

  if (markPts.Count == 0) return false;

  // 4) 画红圈到 Px-Mark-Overlap
  int markLi = EnsureOverlapMarkLayerIndex(doc, parentId);
  if (markLi >= 0)
  {
    ClearOverlapMarksInLayer(doc, markLi);

    foreach (var kv in markPts)
    {
      var pt = kv.Value;
      var circle = new Circle(new Plane(pt, Vector3d.ZAxis), r);

      var a = new ObjectAttributes();
      a.LayerIndex = markLi;
      a.ColorSource = ObjectColorSource.ColorFromLayer;
      a.Name = "OverlapMark|R=200";
      doc.Objects.AddCircle(circle, a);
    }
  }

  doc.Views.Redraw();
  RhinoApp.WriteLine("有轴线重叠，请先清理重叠的轴线。");
  try { Rhino.UI.Dialogs.ShowMessageBox("有轴线重叠，请先清理重叠的轴线，查看Px-Mark-Overlap图层重叠位置标记。", "轴线重叠报错"); } catch { }
  return true;
}
//以上是检测重叠线的辅助函数⬆⬆⬆⬆⬆⬆







// =====================模块02结束==============================





// ===========================================================
// ================   模块03 面板设计模块   ====================
// ===========================================================

class ThicknessPanel : Form
{
  TextBox _txtL;
  TextBox _txtR;

  CheckBox _chkLink;
  Button _btnFlip;
  Button _btnZeroL;
  Button _btnZeroR;
  Button _btnEven;

  // 3.0.4 新增：保温 offset 控件
  TextBox _txtInsulL;
  TextBox _txtInsulR;
  CheckBox _chkInsulL;
  CheckBox _chkInsulR;
  Button _btnInsulFlip;

  // ★ 新增：Wall Generate 六个按钮
  Button _btnGenWalls;       // 绘制墙体
  Button _btnRefresh;        // 刷新
  Button _btnSingleToWall;   // 单线变墙
  Button _btnEditWalls;      // 墙体编辑
  Button _btnFormatBrush;    // 格式刷
  Button _btnToggleCaps;     // 开关封口
  Button _btnRedrawWalls;    // 重绘墙体
  Button _btnRedrawAll;      // 全部重绘
  

  // 3.0.4 新增：是否以保温线作为外轮廓（左右各一档）
  CheckBox _useInsulAsOutlineCheckL;
  CheckBox _useInsulAsOutlineCheckR;
  Label   _useInsulAsOutlineLabel;

  
  // ★ 新增：Wall Type 四个按钮
  Button _btnWallConcrete;
  Button _btnWallBlock;
  Button _btnWallStud;
  Button _btnWallGlass;

  // ★ 新增：Wall Shape 直 / 弧墙按钮
  Button _btnShapeLine;
  Button _btnShapeArc;

  // 统一按钮高亮色：按钮被点击之后变为此高亮颜色
  static readonly Eto.Drawing.Color HighlightColor =
  Eto.Drawing.Color.FromArgb(255, 180, 0);

  const float OutlineOptionFontSize = 11.0f;

  bool _updatingInternally = false;
  bool _linkEnabled = true;
  double _linkedTotal;

  bool _outlineUpdatingInternally = false;

  public double LeftValue       { get; private set; }
  public double RightValue      { get; private set; }
  public double InsulLeftValue  { get; private set; }
  public double InsulRightValue { get; private set; }

  public bool InsulLeftOn  { get { return _chkInsulL.Checked == true; } }
  public bool InsulRightOn { get { return _chkInsulR.Checked == true; } }
  public bool UseInsulLeftAsOutline  { get { return _useInsulAsOutlineCheckL.Checked == true; } }
  public bool UseInsulRightAsOutline { get { return _useInsulAsOutlineCheckR.Checked == true; } }

  
  // ★ 新增：当前选择的墙类型
  public WallKind CurrentWallKind { get; private set; }
  public AxisDrawMode CurrentShapeMode { get; private set; }
  public WallGenerateMode CurrentWallGenMode { get; private set; } = WallGenerateMode.None;

  // ★ 当前是否在执行“绘制墙体”流程（由 RunWallAxisDrawSession 控制）
  public bool IsDrawingWalls { get; set; } = false;

    // ★ 当前是否在执行“单线变墙”流程
  public bool IsSingleToWallMode { get; set; } = false;

  public bool IsTogglingCaps { get; private set; } = false;
  // ★ 当前是否在执行“墙体编辑”流程
  public bool IsEditingWalls { get; set; } = false;
  public bool IsFormatBrushing  { get; set; } = false;
  public bool IsRedrawingWalls  { get; set; } = false;



  
  // ★ 当用户在面板里切换直墙 / 弧墙时，通知外部命令
  public event Action<AxisDrawMode> ShapeModeChanged;

  // ★ 当用户点击“绘制墙体”按钮时，通知外部去启动轴线绘制命令
  public event Action GenerateWallsRequested;
  
  // ★ 当用户点击“刷新”按钮时，通知外部去启动命令
  public event Action RefreshRequested;

  // 在 ThicknessPanel 类里：事件区
  public event Action SingleToWallRequested;

  public event Action RedrawWallsRequested;
  public event Action RedrawAllRequested;
  public event Action EditWallsRequested;
  public event Action FormatBrushRequested;


  public bool Cancelled { get; private set; } = false;

  //墙体编辑定义⬇⬇⬇⬇⬇
  public struct PanelSnapshot
 {
  public double L, R;
  public bool Link;
  public bool InsLOn, InsROn;
  public double InsL, InsR;
  public bool OutlineL, OutlineR;
  public WallKind WallKind;
  public AxisDrawMode ShapeMode;
 }

  public PanelSnapshot CaptureSnapshot()
 {
  return new PanelSnapshot
  {
    L = LeftValue,
    R = RightValue,
    Link = _linkEnabled,
    InsLOn = InsulLeftOn,
    InsROn = InsulRightOn,
    InsL = InsulLeftValue,
    InsR = InsulRightValue,
    OutlineL = UseInsulLeftAsOutline,
    OutlineR = UseInsulRightAsOutline,
    WallKind = CurrentWallKind,
    ShapeMode = CurrentShapeMode
  };
 }

  public void ApplySnapshot(PanelSnapshot s)
 {
  _updatingInternally = true;

  SetShapeMode(s.ShapeMode, false);
  SetWallKind(s.WallKind);

  // ★ 关键修复：同步内部数值（否则 _updatingInternally=true 时 TextChanged 不会更新这些值）
  LeftValue       = s.L;
  RightValue      = s.R;
  InsulLeftValue  = s.InsL;
  InsulRightValue = s.InsR;

  _chkLink.Checked = s.Link;

  _txtL.Text = s.L.ToString();
  _txtR.Text = s.R.ToString();

  _chkInsulL.Checked = s.InsLOn;
  _chkInsulR.Checked = s.InsROn;

  _txtInsulL.Text = s.InsL.ToString();
  _txtInsulR.Text = s.InsR.ToString();

  _useInsulAsOutlineCheckL.Checked = s.OutlineL;
  _useInsulAsOutlineCheckR.Checked = s.OutlineR;

  if (_linkEnabled) _linkedTotal = LeftValue + RightValue;

  _updatingInternally = false;

  RhinoDoc.ActiveDoc?.Views.Redraw();
 }
  //墙体编辑定义⬆⬆⬆⬆⬆




  public ThicknessPanel(double defaultL, double defaultR)
  {
    Title = "PandaBim-Wall2D";
    Resizable = false;
    Padding = new Eto.Drawing.Padding(0);

    // 1. 根据全局状态决定这一次的初始值
    double initL          = defaultL;
    double initR          = defaultR;
    double initInsulL     = 80;
    double initInsulR     = 80;
    bool   initLinkOn     = true;
    bool   initInsulLOn   = false;
    bool   initInsulROn   = false;
    bool   initOutlineLOn = false;
    bool   initOutlineROn = false;
    WallKind initWallKind = WallKind.Block;   // ★ 新增
    AxisDrawMode initShapeMode  = AxisDrawMode.Line;     // 初始：直墙

    if (RTZ3State.HasLastPanelState)
    {
      initL          = RTZ3State.LastLeftThickness;
      initR          = RTZ3State.LastRightThickness;
      initInsulL     = RTZ3State.LastInsulLeftOffset;
      initInsulR     = RTZ3State.LastInsulRightOffset;
      initLinkOn     = RTZ3State.LastLinkChecked;
      initInsulLOn   = RTZ3State.LastInsulLeftChecked;
      initInsulROn   = RTZ3State.LastInsulRightChecked;
      initOutlineLOn = RTZ3State.LastUseInsulOutlineLeft;
      initOutlineROn = RTZ3State.LastUseInsulOutlineRight;
      initWallKind   = RTZ3State.LastWallKind;   // ★ 新增
      initShapeMode  = RTZ3State.LastAxisMode;          // ★ 读上次的直 / 弧
    }

    LeftValue       = initL;
    RightValue      = initR;
    InsulLeftValue  = initInsulL;
    InsulRightValue = initInsulR;
    CurrentWallKind  = initWallKind;             // ★ 新增
    CurrentShapeMode = initShapeMode;                    // ★ 先把属性记住

    _linkEnabled = initLinkOn;
    _linkedTotal = LeftValue + RightValue;

    // ===== 配色 =====
    var dark       = Eto.Drawing.Color.FromArgb(56, 56, 56);
    var lightBox   = Eto.Drawing.Color.FromArgb(180, 180, 180);
    var leftColor  = Eto.Drawing.Color.FromArgb(201, 255, 155);
    var rightColor = Eto.Drawing.Color.FromArgb(255, 144, 173);
    var titleColor = Eto.Drawing.Color.FromArgb(230, 230, 230);
    var buttonBack = Eto.Drawing.Color.FromArgb(90, 90, 90);
    var buttonText = Eto.Drawing.Color.FromArgb(230, 230, 230);

    // ★ Wall Type 被选中时的颜色
    var wallTypeSelectedBack = Eto.Drawing.Color.FromArgb(255, 127, 163);
    var wallTypeSelectedText = Eto.Drawing.Color.FromArgb(50, 50, 50);

    // ===== 尺寸 =====
    int   boxWidth       = 80;
    int   boxHeight      = 36;
    int   insulBoxHeight = (int)(boxHeight * 2 / 3.0);
    float textFontSize   = 15f;
    float buttonFontSize = 8f;
    float insulFontSize  = 12f;
    float commonButtonFontSize = 10f;   // Common Thickness 用的大一点的字
    float wallShapeFontSize   = 10f;   // ★ Wall Shape 字号，想改就在这里改
    // ★ 新增：专门给 WallType / WallShape / Wall Generate 三个区用的统一字号
    float wallOptionFontSize  = 10f;
    int   innerSpacingX  = 6;
    int   innerSpacingY  = 4;
    int   buttonHeight   = 18;
    int   panelSidePadding = 0;
    int   zeroButtonWidth  = 80;
    int   evenButtonWidth  = 56;
    int   flipButtonWidth  = 56;

    var textFont   = new Eto.Drawing.Font("Arial", textFontSize, Eto.Drawing.FontStyle.None);
    var buttonFont = new Eto.Drawing.Font("Arial", buttonFontSize, Eto.Drawing.FontStyle.None);
    var insulFont  = new Eto.Drawing.Font("Arial", insulFontSize, Eto.Drawing.FontStyle.None);
    var commonButtonFont = new Eto.Drawing.Font("Arial", commonButtonFontSize, Eto.Drawing.FontStyle.None);
    // ★ 新增：WallType / WallShape / Wall Generate 区统一用这个字体
    var wallOptionFont = new Eto.Drawing.Font("Arial", wallOptionFontSize, Eto.Drawing.FontStyle.None);

    // ===== 标题 =====
    var titleLabel = new Label
    {
      Text = "Wall Thickness Input",
      TextColor = titleColor,
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None)
    };

    // ===== 主厚度输入框 =====
    _txtL = new TextBox
    {
      Width = boxWidth,
      Height = boxHeight,
      TextAlignment = TextAlignment.Center,
      Text = initL.ToString(CultureInfo.InvariantCulture),
      BackgroundColor = lightBox,
      Font = textFont
    };

    _txtR = new TextBox
    {
      Width = boxWidth,
      Height = boxHeight,
      TextAlignment = TextAlignment.Center,
      Text = initR.ToString(CultureInfo.InvariantCulture),
      BackgroundColor = lightBox,
      Font = textFont
    };

    _txtL.TextChanged += (s, e) =>
    {
      if (_updatingInternally) return;
      if (!TryParse(_txtL.Text, out double v)) return;
      if (v < 0) v = 0;

      if (_linkEnabled)
      {
        double other = _linkedTotal - v;
        if (other < 0) other = 0;
        SetValues(v, other);
      }
      else
      {
        _updatingInternally = true;
        LeftValue = v;
        _updatingInternally = false;
        RhinoDoc.ActiveDoc?.Views.Redraw();
      }
    };

    _txtR.TextChanged += (s, e) =>
    {
      if (_updatingInternally) return;
      if (!TryParse(_txtR.Text, out double v)) return;
      if (v < 0) v = 0;

      if (_linkEnabled)
      {
        double other = _linkedTotal - v;
        if (other < 0) other = 0;
        SetValues(_linkedTotal - v, v);
      }
      else
      {
        _updatingInternally = true;
        RightValue = v;
        _updatingInternally = false;
        RhinoDoc.ActiveDoc?.Views.Redraw();
      }
    };

    // L / R 标记
    var labelL = new Label
    {
      Text = "L",
      TextColor = leftColor,
      VerticalAlignment = VerticalAlignment.Center,
      TextAlignment = TextAlignment.Right,
      Font = new Eto.Drawing.Font("Arial Black", 15, Eto.Drawing.FontStyle.None)
    };

    var labelR = new Label
    {
      Text = "R",
      TextColor = rightColor,
      VerticalAlignment = VerticalAlignment.Center,
      TextAlignment = TextAlignment.Left,
      Font = new Eto.Drawing.Font("Arial Black", 15, Eto.Drawing.FontStyle.None)
    };

    var leftLabelPanel = new Panel
    {
      Padding = new Eto.Drawing.Padding(20, 0, 0, 0),
      Content = labelL
    };

    var rightLabelPanel = new Panel
    {
      Padding = new Eto.Drawing.Padding(0, 0, 20, 0),
      Content = labelR
    };

    // ===== 顶部 0 / EVEN / 0 =====
    _btnZeroL = new Button
    {
      Text = "RESET 0",
      Width = zeroButtonWidth,
      Height = buttonHeight,
      Font = buttonFont,
      TextColor = buttonText,
      BackgroundColor = buttonBack
    };

    _btnEven = new Button
    {
      Text = "EVEN",
      Width = evenButtonWidth,
      Height = buttonHeight,
      Font = buttonFont,
      TextColor = buttonText,
      BackgroundColor = buttonBack
    };

    _btnZeroR = new Button
    {
      Text = "RESET 0",
      Width = zeroButtonWidth,
      Height = buttonHeight,
      Font = buttonFont,
      TextColor = buttonText,
      BackgroundColor = buttonBack
    };

    _btnZeroL.Click += (s, e) =>
    {
      double total = LeftValue + RightValue;
      SetValues(0, total);
    };

    _btnZeroR.Click += (s, e) =>
    {
      double total = LeftValue + RightValue;
      SetValues(total, 0);
    };

    _btnEven.Click += (s, e) =>
    {
      double total = LeftValue + RightValue;
      double half = total / 2.0;
      SetValues(half, half);
    };

    var zeroLeftCell = new StackLayout
    {
      Width = boxWidth,
      Orientation = Orientation.Horizontal,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { _btnZeroL }
    };

    var zeroCenterCell = new StackLayout
    {
      Width = flipButtonWidth,
      Orientation = Orientation.Horizontal,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { _btnEven }
    };

    var zeroRightCell = new StackLayout
    {
      Width = boxWidth,
      Orientation = Orientation.Horizontal,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { _btnZeroR }
    };

    var zerosRow = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = innerSpacingX,
      HorizontalContentAlignment = HorizontalAlignment.Left,
      Items = { zeroLeftCell, zeroCenterCell, zeroRightCell }
    };

    // ===== 箭头行 =====
    const int ArrowLeftSpaceCount  = 8;
    const int ArrowRightSpaceCount = 32;

    string arrowLeftPad  = new string(' ', ArrowLeftSpaceCount);
    string arrowRightPad = new string(' ', ArrowRightSpaceCount);

    var arrowL = new Label
    {
      Text = arrowLeftPad + "▼",
      TextColor = leftColor,
      TextAlignment = TextAlignment.Left,
      VerticalAlignment = VerticalAlignment.Center
    };

    var arrowR = new Label
    {
      Text = arrowRightPad + "▼",
      TextColor = rightColor,
      TextAlignment = TextAlignment.Right,
      VerticalAlignment = VerticalAlignment.Center
    };

    var arrowsRow = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = innerSpacingX,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { arrowL, arrowR }
    };

    // ===== 中间 FLIP + LINK =====
    _btnFlip = new Button
    {
      Text = "FLIP",
      Width = flipButtonWidth,
      Height = buttonHeight,
      Font = buttonFont,
      TextColor = buttonText,
      BackgroundColor = buttonBack
    };

    _btnFlip.Click += (s, e) =>
    {
      SetValues(RightValue, LeftValue);
    };

    _chkLink = new CheckBox { Checked = initLinkOn };

    _chkLink.CheckedChanged += (s, e) =>
    {
      _linkEnabled = _chkLink.Checked == true;
      if (_linkEnabled)
        _linkedTotal = LeftValue + RightValue;
    };

    var linkLabel = new Label
    {
      Text = "LINK",
      TextColor = buttonText,
      Font = new Eto.Drawing.Font("Arial", buttonFontSize, Eto.Drawing.FontStyle.None),
      VerticalAlignment = VerticalAlignment.Center,
      TextAlignment = TextAlignment.Left
    };

    var linkRow = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 4,
      Items = { _chkLink, linkLabel }
    };

    var centerStack = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 2,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { _btnFlip, linkRow },
      Width = flipButtonWidth
    };

    // ===== 保温输入框（更扁） =====
    var insulOffBack = Eto.Drawing.Color.FromArgb(80, 80, 80);
    var insulOffText = Eto.Drawing.Color.FromArgb(110, 110, 110);

    var insulOnBack  = lightBox;
    var insulOnText  = Eto.Drawing.Colors.Black;

    _txtInsulL = new TextBox
    {
      Width = boxWidth,
      Height = insulBoxHeight,
      TextAlignment = TextAlignment.Center,
      Text = initInsulL.ToString(CultureInfo.InvariantCulture),
      BackgroundColor = insulOffBack,
      TextColor       = insulOffText,
      Font = insulFont,
      Enabled = false
    };

    _txtInsulR = new TextBox
    {
      Width = boxWidth,
      Height = insulBoxHeight,
      TextAlignment = TextAlignment.Center,
      Text = initInsulR.ToString(CultureInfo.InvariantCulture),
      BackgroundColor = insulOffBack,
      TextColor       = insulOffText,
      Font = insulFont,
      Enabled = false
    };

    _txtInsulL.TextChanged += (s, e) =>
    {
      if (_updatingInternally) return;
      if (!TryParse(_txtInsulL.Text, out double v)) return;
      if (v < 0) v = 0;
      InsulLeftValue = v;
      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    _txtInsulR.TextChanged += (s, e) =>
    {
      if (_updatingInternally) return;
      if (!TryParse(_txtInsulR.Text, out double v)) return;
      if (v < 0) v = 0;
      InsulRightValue = v;
      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    _chkInsulL = new CheckBox { Checked = initInsulLOn };
    _chkInsulR = new CheckBox { Checked = initInsulROn };

    _chkInsulL.CheckedChanged += (s, e) =>
    {
      bool on = _chkInsulL.Checked == true;
      _txtInsulL.Enabled         = on;
      _txtInsulL.BackgroundColor = on ? insulOnBack : insulOffBack;
      _txtInsulL.TextColor       = on ? insulOnText : insulOffText;
      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    _chkInsulR.CheckedChanged += (s, e) =>
    {
      bool on = _chkInsulR.Checked == true;
      _txtInsulR.Enabled         = on;
      _txtInsulR.BackgroundColor = on ? insulOnBack : insulOffBack;
      _txtInsulR.TextColor       = on ? insulOnText : insulOffText;
      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    // 初始化一次勾选外观
    {
      bool onL = _chkInsulL.Checked == true;
      _txtInsulL.Enabled         = onL;
      _txtInsulL.BackgroundColor = onL ? insulOnBack : insulOffBack;
      _txtInsulL.TextColor       = onL ? insulOnText : insulOffText;

      bool onR = _chkInsulR.Checked == true;
      _txtInsulR.Enabled         = onR;
      _txtInsulR.BackgroundColor = onR ? insulOnBack : insulOffBack;
      _txtInsulR.TextColor       = onR ? insulOnText : insulOffText;
    }

    _btnInsulFlip = new Button
    {
      Text = "FLIP",
      Width = flipButtonWidth,
      Height = buttonHeight,
      Font = buttonFont,
      TextColor = buttonText,
      BackgroundColor = buttonBack
    };

    _btnInsulFlip.Click += (s, e) =>
    {
      string tmpText = _txtInsulL.Text;
      _txtInsulL.Text = _txtInsulR.Text;
      _txtInsulR.Text = tmpText;

      bool leftOn  = _chkInsulL.Checked == true;
      bool rightOn = _chkInsulR.Checked == true;

      _chkInsulL.Checked = rightOn;
      _chkInsulR.Checked = leftOn;
    };

    var insulFlipCenter = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items = { _btnInsulFlip }
    };

    var insulationCenter = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(innerSpacingX, 0),
      Rows =
      {
        new TableRow(_txtInsulL, insulFlipCenter, _txtInsulR)
      }
    };

    var thicknessCenter = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(innerSpacingX, 0),
      Rows =
      {
        new TableRow(_txtL, centerStack, _txtR)
      }
    };

    // ===== 底部“保温线作为建筑外轮廓线”（左右互斥勾选） =====
    _useInsulAsOutlineCheckL = new CheckBox { Checked = initOutlineLOn };
    _useInsulAsOutlineCheckR = new CheckBox { Checked = initOutlineROn };
    _useInsulAsOutlineLabel = new Label
    {
      Text = "←-- 面层线作为建筑外轮廓线 --→",
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", OutlineOptionFontSize, Eto.Drawing.FontStyle.None),
      TextColor = Eto.Drawing.Color.FromArgb(140, 140, 140),
      VerticalAlignment = VerticalAlignment.Center
    };

    _useInsulAsOutlineCheckL.CheckedChanged += (s, e) =>
    {
      if (_outlineUpdatingInternally) return;

      _outlineUpdatingInternally = true;

      if (_useInsulAsOutlineCheckL.Checked == true)
        _useInsulAsOutlineCheckR.Checked = false;

      _outlineUpdatingInternally = false;

      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    _useInsulAsOutlineCheckR.CheckedChanged += (s, e) =>
    {
      if (_outlineUpdatingInternally) return;

      _outlineUpdatingInternally = true;

      if (_useInsulAsOutlineCheckR.Checked == true)
        _useInsulAsOutlineCheckL.Checked = false;

      _outlineUpdatingInternally = false;

      RhinoDoc.ActiveDoc?.Views.Redraw();
    };

    var leftInsulPanel = new Panel
    {
      Padding = new Eto.Drawing.Padding(30, 0, 0, 0),
      Content = _chkInsulL
    };

    var leftOutlinePanel = new Panel
    {
      Padding = new Eto.Drawing.Padding(30, 0, 0, 0),
      Content = _useInsulAsOutlineCheckL
    };




    // ===== Common Thickness 快速墙厚按钮 =====


    var commonTitleLabel = new Label
    {
     Text = "Common Thickness",
     TextAlignment = TextAlignment.Center,
        Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None),
     TextColor = titleColor
    };

    // 8 个按钮：100|100, 120|120, 125|125, 150|150, 30|30, 50|50, 60|60, 75|75
    int commonBtnWidth  = 105;
    int commonBtnHeight = 26;
    // 水平间距单独设大一点
    int commonSpacingX = 22;

    Button MakeCommonButton(string text, double thickness)
    {
     var btn = new Button
     {
       Text = text,
      Width = commonBtnWidth,
      Height = commonBtnHeight,
      Font = commonButtonFont,
        TextColor = buttonText,
       BackgroundColor = buttonBack
     };

     btn.Click += (s, e) =>
     {
       // 左右同时设置为相同厚度
       SetValues(thickness, thickness);
     };

     return btn;
    }

    var btn100 = MakeCommonButton("100 | 100", 100.0);
    var btn120 = MakeCommonButton("120 | 120", 120.0);
    var btn125 = MakeCommonButton("125 | 125", 125.0);
    var btn150 = MakeCommonButton("150 | 150", 150.0);
    var btn30  = MakeCommonButton("30 | 30",   30.0);
    var btn50  = MakeCommonButton("50 | 50",   50.0);
    var btn60  = MakeCommonButton("60 | 60",   60.0);
    var btn75  = MakeCommonButton("75 | 75",   75.0);

    // 2 列 × 4 行的表格排布
    var commonGrid = new TableLayout
    {
     Padding = new Eto.Drawing.Padding(0, 0, 0, 0),                  // 整块左右再留点空
     Spacing = new Eto.Drawing.Size(commonSpacingX, innerSpacingY),    // 水平 spacing 更大
      Rows =
      {
        new TableRow(btn100, btn120),
        new TableRow(btn125, btn150),
        new TableRow(btn30,  btn50),
        new TableRow(btn60,  btn75)
      }
    };

    // 整个 Common Thickness 区域：标题 + 网格，整体居中
    var commonSection = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 4,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items =
      {
        commonTitleLabel,
        commonGrid
      }
    };

    // ===== Wall Type =====
    var wallTypeLabel = new Label
    {
      Text = "Wall Type",
      TextColor = titleColor,
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None)
    };

    // 让 Wall Type 按钮和 Common Thickness 一样宽高
    int wallTypeButtonWidth  = commonBtnWidth;
    int wallTypeButtonHeight = commonBtnHeight;

    _btnWallConcrete = new Button
    {
      Text = "钢筋混凝土墙",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font = wallOptionFont
    };

    _btnWallBlock = new Button
    {
      Text = "填充墙",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font = wallOptionFont
    };

    _btnWallStud = new Button
    {
      Text = "轻质隔墙",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font = wallOptionFont
    };

    _btnWallGlass = new Button
    {
      Text = "玻璃幕墙",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font = wallOptionFont
    };

    // 点击按钮时，更新墙类型
    _btnWallConcrete.Click += (s, e) => { SetWallKind(WallKind.Concrete); };
    _btnWallBlock.Click    += (s, e) => { SetWallKind(WallKind.Block); };
    _btnWallStud.Click     += (s, e) => { SetWallKind(WallKind.Stud); };
    _btnWallGlass.Click    += (s, e) => { SetWallKind(WallKind.Glass); };

    // 2×2 布局：用和 Common Thickness 完全一样的 Padding / Spacing
    var wallTypeGrid = new TableLayout
    {
     Padding = new Eto.Drawing.Padding(0, 0, 0, 0),                   // 和 commonGrid 一样
     Spacing = new Eto.Drawing.Size(commonSpacingX, innerSpacingY),     // 水平间距也用 commonSpacingX
     Rows =
      {
        new TableRow(_btnWallConcrete, _btnWallBlock),
        new TableRow(_btnWallStud,     _btnWallGlass)
      }
    };


    // 先按当前 CurrentWallKind 做一遍着色
    ApplyWallTypeButtonStyle();

    // 整个 Wall Type 区域：标题 + 按钮网格
    var wallTypeSection = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 4,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items =
      {
        wallTypeLabel,
        wallTypeGrid
      }
    };

    // ===== Wall Shape ===== 墙种类：直墙 弧墙
    var wallShapeLabel = new Label
        {
      Text = "Wall Shape",
      TextColor = titleColor,
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None)
        };

        int wallShapeButtonWidth  = commonBtnWidth;   // 和前面按钮保持一致
        int wallShapeButtonHeight = commonBtnHeight;
        var wallShapeFont = new Eto.Drawing.Font("Arial", wallShapeFontSize, Eto.Drawing.FontStyle.None);

    _btnShapeLine = new Button
    {
      Text  = "直墙",
      Width = wallShapeButtonWidth,
      Height = wallShapeButtonHeight,
      Font  = wallOptionFont
    };

    _btnShapeArc = new Button
    {
      Text  = "弧墙",
      Width = wallShapeButtonWidth,
      Height = wallShapeButtonHeight,
      Font  = wallOptionFont
    };

    // 点击按钮 → 切换直墙 / 弧墙
    _btnShapeLine.Click += (s, e) => { SetShapeMode(AxisDrawMode.Line); };
    _btnShapeArc.Click  += (s, e) => { SetShapeMode(AxisDrawMode.Arc); };

    // 1 行 2 列，Padding / Spacing 和前面完全一致
    var wallShapeGrid = new TableLayout
    {
      Padding = new Eto.Drawing.Padding(0, 0, 0, 0),
      Spacing = new Eto.Drawing.Size(commonSpacingX, innerSpacingY),
      Rows =
      {
        new TableRow(_btnShapeLine, _btnShapeArc)
      }
    };

    var wallShapeSection = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 4,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items =
      {
        wallShapeLabel,
        wallShapeGrid
      }
    };

        // ===== Wall Generate =====
    var wallGenLabel = new Label
    {
      Text = "Wall Generate",
      TextColor = titleColor,
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None)
    };

    // 按钮尺寸、字体：完全复用 Wall Type 的设置
    
    // ★ 新增：Wall Edit 标题，给下面四个编辑类按钮用（编辑墙体/格式刷/重绘墙体/全部重绘）
    var wallEditLabel = new Label
    {
      Text = "Wall Edit",
      TextColor = titleColor,
      TextAlignment = TextAlignment.Center,
      Font = new Eto.Drawing.Font("Arial", 10, Eto.Drawing.FontStyle.None)
    };

   
    _btnGenWalls = new Button
    {
      Text  = "Dw 绘制墙体",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "连续画轴线并生成墙线"
    };

    _btnRefresh = new Button
    {
      Text  = "Re 刷新",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "改动轴线后重新生成墙线"
    };

    _btnSingleToWall = new Button
    {
      Text  = "Sw 单线变墙",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "选取单线变为双线墙"
    };

    _btnEditWalls = new Button
    {
      Text  = "Ed 编辑墙体",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "选取轴线编辑墙体数据"
    };

    _btnFormatBrush = new Button
    {
      Text  = "Br 格式刷",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "就是字面意思"
    };

    _btnToggleCaps = new Button
    {
      Text  = "Cw 开关封口",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "开启或关闭墙的端头线"
    };

        _btnRedrawWalls = new Button
    {
      Text  = "Rd 重绘墙体",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "选取轴线重新生成墙体"
    };

    _btnRedrawAll = new Button
    {
      Text  = "Ra 全部重绘",
      Width = wallTypeButtonWidth,
      Height = wallTypeButtonHeight,
      Font  = wallOptionFont,
      BackgroundColor = buttonBack,
      TextColor       = buttonText,
      ToolTip = "重新生成所有墙线"
    };


    // ==== 六个按钮的点击逻辑 ====

    // 绘制墙体：如果没在画墙 → 先高亮，再触发外部绘制流程；
    // 如果当前已经在画墙，就只提示一句，不重复进入。
    _btnGenWalls.Click += (s, e) =>
    {
      if (IsDrawingWalls)       {RhinoApp.WriteLine("正在绘制墙体");return;}
      if (IsSingleToWallMode)   { RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsTogglingCaps)       {RhinoApp.WriteLine("请先结束开关封口命令");return;}
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;}
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }
      if (IsFormatBrushing)     { RhinoApp.WriteLine("请先结束格式刷命令"); return; }

      SetWallGenerateMode(WallGenerateMode.Generate);   // 按钮立即变色
      GenerateWallsRequested?.Invoke();                 // 通知外部开始 RunWallAxisDrawSession
    };

    // 下面这 5 个：如果正在绘制墙体 → 只提示“请先结束墙体绘制”，不切换模式、不执行功能。
    _btnRefresh.Click += (s, e) =>
    {
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsSingleToWallMode)   {RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsTogglingCaps)       {RhinoApp.WriteLine("请先结束开关封口命令");return;}
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;}
      if (IsFormatBrushing)     { RhinoApp.WriteLine("请先结束格式刷命令"); return; }
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }     
      SetWallGenerateMode(WallGenerateMode.Refresh);
        // TODO：这里以后挂“刷新”逻辑
        // ✅ 通知外部执行刷新
      RefreshRequested?.Invoke();
    };

    _btnSingleToWall.Click += (s, e) =>
    {
      if (IsSingleToWallMode)   {RhinoApp.WriteLine("单线变墙命令正在执行中");return;}
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsTogglingCaps)       {RhinoApp.WriteLine("请先结束开关封口命令");return;}  
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;} 
      if (IsFormatBrushing)     { RhinoApp.WriteLine("请先结束格式刷命令"); return; }
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }         
      // ✅ 用“绘制墙体”同一套方法，让按钮立刻变亮
      SetWallGenerateMode(WallGenerateMode.SingleToWall);
      // 这里只发事件，不直接做 Rhino 的选择操作
      SingleToWallRequested?.Invoke();

    };

    _btnEditWalls.Click += (s, e) =>
    {
      if (IsEditingWalls)     {RhinoApp.WriteLine("墙体编辑命令正在执行中");return;}
      if (IsDrawingWalls)     {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsSingleToWallMode) {RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsTogglingCaps)     {RhinoApp.WriteLine("请先结束开关封口命令");return;}
      if (IsFormatBrushing)     { RhinoApp.WriteLine("请先结束格式刷命令"); return; }
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }

        IsEditingWalls = true;
        SetWallGenerateMode(WallGenerateMode.Edit);

        try
      {
        EditWallsRequested?.Invoke(); // 你外部挂的 RunEditWallsMode
      }
        finally
      {
        IsEditingWalls = false;
        SetWallGenerateMode(WallGenerateMode.None);
        RhinoDoc.ActiveDoc?.Views.Redraw();
      }
    };

    _btnFormatBrush.Click += (s, e) =>
    {
      if (IsFormatBrushing)     {RhinoApp.WriteLine("正在执行格式刷命令"); return; }
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsSingleToWallMode)   {RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsTogglingCaps)       {RhinoApp.WriteLine("请先结束开关封口命令");return;}
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;} 
      if (IsRedrawingWalls)     {RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }           
      IsFormatBrushing = true;
      SetWallGenerateMode(WallGenerateMode.FormatBrush);

      try
     {
        FormatBrushRequested?.Invoke();   // 外部挂 RunFormatBrushMode(doc, panel)
     }
        finally
     {
        IsFormatBrushing = false;
        SetWallGenerateMode(WallGenerateMode.None);
        RhinoDoc.ActiveDoc?.Views.Redraw();
     }
    };

    _btnToggleCaps.Click += (s, e) =>
    {
      if (IsTogglingCaps)       {RhinoApp.WriteLine("正在执行开关封口");return;}
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsSingleToWallMode)   {RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;}    
      if (IsFormatBrushing)     {RhinoApp.WriteLine("请先结束格式刷命令"); return; }
      if (IsRedrawingWalls)     {RhinoApp.WriteLine("请先结束重绘墙体命令"); return; }         
      IsTogglingCaps = true;
      SetWallGenerateMode(WallGenerateMode.ToggleCaps);

      try
      {
        ToggleWallCapByPick(RhinoDoc.ActiveDoc);
      }
        finally
      {
        IsTogglingCaps = false;
        SetWallGenerateMode(WallGenerateMode.None);
        RhinoDoc.ActiveDoc?.Views.Redraw();
      }       


      //SetWallGenerateMode(WallGenerateMode.ToggleCaps);
      // TODO：这里以后挂“开关封口”逻辑
      //ToggleWallCapByPick(RhinoDoc.ActiveDoc);
    };


        _btnRedrawWalls.Click += (s, e) =>
    {
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("正在执行重绘墙体"); return; }
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制");return;}
      if (IsSingleToWallMode)   {RhinoApp.WriteLine("请先结束单线变墙");return;}
      if (IsTogglingCaps)       {RhinoApp.WriteLine("请先结束开关封口");return;}
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑");return;}
      if (IsFormatBrushing)     {RhinoApp.WriteLine("请先结束格式刷"); return; }      
        IsRedrawingWalls = true;
        SetWallGenerateMode(WallGenerateMode.RedrawWalls);

        try
     {
        RedrawWallsRequested?.Invoke();
     }
        finally
     {
        IsRedrawingWalls = false;
        SetWallGenerateMode(WallGenerateMode.None);
        RhinoDoc.ActiveDoc?.Views.Redraw();
     }

    };

    _btnRedrawAll.Click += (s, e) =>
    {
      if (IsDrawingWalls)       {RhinoApp.WriteLine("请先结束墙体绘制"); return; }
      if (IsSingleToWallMode)   { RhinoApp.WriteLine("请先结束单线变墙命令");return;}
      if (IsTogglingCaps)       { RhinoApp.WriteLine("请先结束开关封口命令");return;} 
      if (IsEditingWalls)       {RhinoApp.WriteLine("请先结束墙体编辑命令");return;} 
      if (IsRedrawingWalls)     { RhinoApp.WriteLine("请先结束重绘墙体命令"); return; } 
      if (IsFormatBrushing)     { RhinoApp.WriteLine("请先结束格式刷命令"); return; }  
            
        // ✅ 弹窗确认：Yes / No
        var dr = Eto.Forms.MessageBox.Show(
        this,                               // 以面板为父窗口
        "全部重绘可能会造成卡顿，是否继续？",
        "提示",
        Eto.Forms.MessageBoxButtons.YesNo,
        Eto.Forms.MessageBoxType.Warning);

        if (dr != Eto.Forms.DialogResult.Yes)
      {
        // 点“否” -> 直接结束，什么都不做
        RhinoApp.WriteLine("已取消全部重绘。");
        return;
      }
      SetWallGenerateMode(WallGenerateMode.RedrawAll);
      // TODO：这里以后挂“全部重绘”的实际逻辑
      RedrawAllRequested?.Invoke();
    };



    //  ………………………………………………………………按钮的排列顺序 ……………………………………………………………………………
    // 1 行：绘制墙体 | 刷新
    // 2 行：单线变墙 | 开关封口
    // 3 行：编辑墙体 | 格式刷
    // 4 行：重绘墙体 | 全部重绘
    var wallGenGrid = new TableLayout
    {
      Padding = new Eto.Drawing.Padding(0, 0, 0, 0),
      Spacing = new Eto.Drawing.Size(commonSpacingX, innerSpacingY),
      Rows =
      {
        new TableRow(_btnGenWalls,     _btnRefresh),
        new TableRow(_btnSingleToWall, _btnToggleCaps),
      }
    };

    // ★ 新增：Wall Edit 区域，放 编辑墙体 / 格式刷 / 重绘墙体 / 全部重绘 四个按钮
    var wallEditGrid = new TableLayout
    {
     Padding = new Eto.Drawing.Padding(0, 0, 0, 0),
     Spacing = new Eto.Drawing.Size(commonSpacingX, innerSpacingY),
     Rows =
     {
        // 第三行：编辑墙体 / 格式刷
        new TableRow(_btnEditWalls,    _btnFormatBrush),

       // 第四行：重绘墙体 / 全部重绘
        new TableRow(_btnRedrawWalls,  _btnRedrawAll)
      }
    };



    // ★ 初始化 Wall Generate 按钮样式（默认：都不选，全是灰色）
    ApplyWallGenerateButtonStyle();

    var wallGenerateSection = new StackLayout
    {
      Orientation = Orientation.Vertical,
      Spacing = 4,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      Items =
      {
       // 上半块：Wall Generate
        wallGenLabel,
        wallGenGrid,

        // 下半块：Wall Edit
        wallEditLabel,
        wallEditGrid
      }
    };


    

    // ★ 构造完按钮后，用初始模式刷一遍样式（不触发事件）
    SetShapeMode(initShapeMode, false);


    // ===== 主体：3 列 TableLayout =====
    var main = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(innerSpacingX, innerSpacingY),
      Rows =
      {
        new TableRow(null, zerosRow, null),
        new TableRow(null, arrowsRow, null),
        new TableRow(leftLabelPanel, thicknessCenter, rightLabelPanel),
        new TableRow(leftInsulPanel, insulationCenter, _chkInsulR),
        new TableRow(leftOutlinePanel, _useInsulAsOutlineLabel, _useInsulAsOutlineCheckR),
          
        new TableRow(null, commonSection, null), // ★ 把 Common Thickness 整块插进来（包含标题 + 8 个按钮）
        new TableRow(null, wallTypeSection, null),  // Wall Type 整块放在最下面
        new TableRow(null, wallShapeSection, null),   // ★ 新增：Wall Shape 在最底下
        new TableRow(null, wallGenerateSection, null) // ★ 最底部：Wall Generate 六个按钮
      }
    };


    // ===== 最外层（标题 + 主体） =====


    var outer = new TableLayout
    {
      Padding = new Eto.Drawing.Padding(panelSidePadding, 14, panelSidePadding, 14),
      Spacing = new Eto.Drawing.Size(0, 8),
      Rows =
      {
        new TableRow(titleLabel),
        new TableRow(main),
      }
    };

    var root = new Panel
    {
      BackgroundColor = dark,
      Content = outer,
      Padding = new Eto.Drawing.Padding(0)
    };

    Content = root;

    // Esc 关闭
    _chkLink.KeyDown                 += HandleKey;
    _chkInsulL.KeyDown               += HandleKey;
    _chkInsulR.KeyDown               += HandleKey;
    _useInsulAsOutlineCheckL.KeyDown += HandleKey;
    _useInsulAsOutlineCheckR.KeyDown += HandleKey;
    KeyDown                          += HandleKey;
    _txtL.KeyDown                    += HandleKey;
    _txtR.KeyDown                    += HandleKey;
    _txtInsulL.KeyDown               += HandleKey;
    _txtInsulR.KeyDown               += HandleKey;

    Closed += (s, e) =>
    {
      RTZ3State.LastLocation = this.Location;
      RTZ3State.HasLastLocation = true;

      RTZ3State.LastLeftThickness  = LeftValue;
      RTZ3State.LastRightThickness = RightValue;

      double insL = 0.0, insR = 0.0;
      TryParse(_txtInsulL.Text, out insL);
      TryParse(_txtInsulR.Text, out insR);
      RTZ3State.LastInsulLeftOffset  = insL;
      RTZ3State.LastInsulRightOffset = insR;

      RTZ3State.LastLinkChecked          = _chkLink.Checked == true;
      RTZ3State.LastInsulLeftChecked     = _chkInsulL.Checked == true;
      RTZ3State.LastInsulRightChecked    = _chkInsulR.Checked == true;
      RTZ3State.LastUseInsulOutlineLeft  = _useInsulAsOutlineCheckL.Checked == true;
      RTZ3State.LastUseInsulOutlineRight = _useInsulAsOutlineCheckR.Checked == true;

      // ★ 保存墙类型 + 直 / 弧模式
      RTZ3State.LastWallKind = this.CurrentWallKind;
      RTZ3State.LastAxisMode = this.CurrentShapeMode;

      RTZ3State.HasLastPanelState = true;

      if (!Cancelled)
        Cancelled = true;
    };
  }

    // ★ 根据当前墙类型给四个按钮着色
  void ApplyWallTypeButtonStyle()
  {
    if (_btnWallConcrete == null) return;

    void SetStyle(Button btn, bool selected)
    {
      if (btn == null) return;

      if (selected)
      {
        btn.BackgroundColor = HighlightColor; // ← 统一高亮色 255,180,0
        btn.TextColor       = Eto.Drawing.Color.FromArgb(50, 50, 50);
      }
      else
      {
        btn.BackgroundColor = Eto.Drawing.Color.FromArgb(90, 90, 90);
        btn.TextColor       = Eto.Drawing.Color.FromArgb(230, 230, 230);
      }
    }

    SetStyle(_btnWallConcrete, CurrentWallKind == WallKind.Concrete);
    SetStyle(_btnWallBlock,    CurrentWallKind == WallKind.Block);
    SetStyle(_btnWallStud,     CurrentWallKind == WallKind.Stud);
    SetStyle(_btnWallGlass,    CurrentWallKind == WallKind.Glass);
  }

    // ★ 根据当前 WallGenerateMode 给 6 个按钮着色（配色与 Wall Type 完全一致）
  void ApplyWallGenerateButtonStyle()
  {
    if (_btnGenWalls == null) return;

    void SetStyle(Button btn, bool selected)
    {
      if (btn == null) return;

      if (selected)
      {
        // 选中：粉色背景 + 深色文字（和 WallType 一样）
        btn.BackgroundColor = HighlightColor; // ← 统一高亮色 255,180,0
        btn.TextColor       = Eto.Drawing.Color.FromArgb(50, 50, 50);
      }
      else
      {
        // 未选中：深灰背景 + 亮文字（和其它按钮一致）
        btn.BackgroundColor = Eto.Drawing.Color.FromArgb(90, 90, 90);
        btn.TextColor       = Eto.Drawing.Color.FromArgb(230, 230, 230);
      }
    }

    SetStyle(_btnGenWalls,     CurrentWallGenMode == WallGenerateMode.Generate);
    SetStyle(_btnRefresh,      CurrentWallGenMode == WallGenerateMode.Refresh);
    SetStyle(_btnSingleToWall, CurrentWallGenMode == WallGenerateMode.SingleToWall);
    SetStyle(_btnEditWalls,    CurrentWallGenMode == WallGenerateMode.Edit);
    SetStyle(_btnFormatBrush,  CurrentWallGenMode == WallGenerateMode.FormatBrush);
    SetStyle(_btnToggleCaps,   CurrentWallGenMode == WallGenerateMode.ToggleCaps);
    SetStyle(_btnRedrawWalls,  CurrentWallGenMode == WallGenerateMode.RedrawWalls);
    SetStyle(_btnRedrawAll,    CurrentWallGenMode == WallGenerateMode.RedrawAll);

  }

    // ★ 外部 / 内部都用这个来切换当前 Wall Generate 模式
  public void SetWallGenerateMode(WallGenerateMode mode)
  {
    CurrentWallGenMode = mode;
    ApplyWallGenerateButtonStyle();
    RhinoDoc.ActiveDoc?.Views.Redraw();
  }





  // ★ 外部调用：设置墙类型（按钮点击 / 命令行选项切换都会用到）
  public void SetWallKind(WallKind kind)
  {
    CurrentWallKind = kind;
    ApplyWallTypeButtonStyle();
    RhinoDoc.ActiveDoc?.Views.Redraw();
  }
  
   // ★ 根据当前直 / 弧模式给两个按钮着色
  void ApplyWallShapeButtonStyle()
  {
    if (_btnShapeLine == null || _btnShapeArc == null) return;

    var normalBack = Eto.Drawing.Color.FromArgb(90, 90, 90);
    var normalText = Eto.Drawing.Color.FromArgb(230, 230, 230);

    var selBack = HighlightColor; // ← 统一高亮色 255,180,0
    var selText = Eto.Drawing.Color.FromArgb(50, 50, 50);

    if (CurrentShapeMode == AxisDrawMode.Line)
    {
      _btnShapeLine.BackgroundColor = selBack;
      _btnShapeLine.TextColor       = selText;
      _btnShapeArc.BackgroundColor  = normalBack;
      _btnShapeArc.TextColor        = normalText;
    }
    else
    {
      _btnShapeLine.BackgroundColor = normalBack;
      _btnShapeLine.TextColor       = normalText;
      _btnShapeArc.BackgroundColor  = selBack;
      _btnShapeArc.TextColor        = selText;
    }
  }

  // ★ 外部 & 内部都用这个来切换直 / 弧墙
  public void SetShapeMode(AxisDrawMode mode, bool raiseEvent = true)
  {
    CurrentShapeMode = mode;
    ApplyWallShapeButtonStyle();
    RhinoDoc.ActiveDoc?.Views.Redraw();

    if (raiseEvent && ShapeModeChanged != null)
      ShapeModeChanged(mode);
  }

    // ★ 供外部调用：进入/退出单线变墙模式时，统一控制 UI 状态
  public void SetSingleToWallMode(bool active)
  {
    IsSingleToWallMode = active;

    // 单线变墙时，直/弧墙按钮对当前命令无效：统一灰掉
    if (_btnShapeLine != null && _btnShapeArc != null)
    {
      _btnShapeLine.Enabled = !active;
      _btnShapeArc.Enabled  = !active;
    }

    if (!active)
    {
      // 退出时恢复直/弧按钮的高亮状态
      // （下面这个方法你原来就有：根据 CurrentShapeMode 给两个按钮着色）
      ApplyWallShapeButtonStyle();
    }
  }

  // ★ 仅第一次使用单线变墙时，把面板参数重置为题目要求的默认值
  public void ResetForSingleToWallDefaults()
  {
    _updatingInternally = true;

    // 1）墙厚 L / R = 100
    LeftValue  = 100.0;
    RightValue = 100.0;
    _linkedTotal = LeftValue + RightValue;
    _txtL.Text = "100";
    _txtR.Text = "100";

    // 2）保温：默认关闭
    _chkInsulL.Checked = false;
    _chkInsulR.Checked = false;

    // 让外观跟勾选事件里一致（灰色、不可编辑）
    _txtInsulL.Enabled         = false;
    _txtInsulL.BackgroundColor = Eto.Drawing.Color.FromArgb(80, 80, 80);
    _txtInsulL.TextColor       = Eto.Drawing.Color.FromArgb(110, 110, 110);

    _txtInsulR.Enabled         = false;
    _txtInsulR.BackgroundColor = Eto.Drawing.Color.FromArgb(80, 80, 80);
    _txtInsulR.TextColor       = Eto.Drawing.Color.FromArgb(110, 110, 110);

    // 3）“保温线作为外轮廓线”默认全部关闭
    _useInsulAsOutlineCheckL.Checked = false;
    _useInsulAsOutlineCheckR.Checked = false;

    // 4）墙体类型默认 = 填充墙
    SetWallKind(WallKind.Block);

    _updatingInternally = false;

    RhinoDoc.ActiveDoc?.Views.Redraw();
  }

 

  void SetValues(double newL, double newR)
  {
    if (newL < 0) newL = 0;
    if (newR < 0) newR = 0;

    _updatingInternally = true;

    LeftValue  = newL;
    RightValue = newR;

    _txtL.Text = newL.ToString(CultureInfo.InvariantCulture);
    _txtR.Text = newR.ToString(CultureInfo.InvariantCulture);

    _updatingInternally = false;

    if (_linkEnabled)
      _linkedTotal = LeftValue + RightValue;

    RhinoDoc.ActiveDoc?.Views.Redraw();
  }

  void HandleKey(object sender, Eto.Forms.KeyEventArgs e)
  {
    if (e.Key == Eto.Forms.Keys.Escape)
    {
      e.Handled = true;
      Cancelled = true;
      Close();
    }

    if (e.Key == Eto.Forms.Keys.Space && sender is CheckBox)
    {
      e.Handled = true;
      return;
    }
  }

  bool TryParse(string s, out double v)
  {
    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
        || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture,   out v);
  }
  // ===================== 开关封口：点选轴线端头 =====================
   void ToggleWallCapByPick(RhinoDoc doc)
 {
   if (doc == null) return;

   // 以“当前图层所在父级”作为分区限制
   int curLi = doc.Layers.CurrentLayerIndex;
   Guid parentId = Guid.Empty;
   if (curLi >= 0)
   {
    var curLay = doc.Layers[curLi];
    if (curLay != null) parentId = curLay.ParentLayerId;
   }

   bool IsLayerUnderParent(int layerIndex, Guid pid)
   {
    if (pid == Guid.Empty) return true;
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count) return false;

    Layer lay = doc.Layers[layerIndex];
    while (lay != null)
    {
      if (lay.Id == pid) return true;
      if (lay.ParentLayerId == Guid.Empty) break;
      lay = doc.Layers.FindId(lay.ParentLayerId);
    }
    return false;
   }

   bool IsAxisLayer(int layerIndex)
   {
    if (!IsLayerUnderParent(layerIndex, parentId)) return false;
    var lay = doc.Layers[layerIndex];
    return lay != null && lay.Name.Equals("PB-Wall-Axis", StringComparison.OrdinalIgnoreCase);
   }

   bool IsWallLayer(int layerIndex)
   {
    if (!IsLayerUnderParent(layerIndex, parentId)) return false;
    var lay = doc.Layers[layerIndex];
    if (lay == null) return false;
    var n = lay.Name ?? "";
    return n.Equals("PB-Wall", StringComparison.OrdinalIgnoreCase)
        || n.Equals("PB-Wall-Glas", StringComparison.OrdinalIgnoreCase)
        || n.Equals("PB-Wall-Stud", StringComparison.OrdinalIgnoreCase);
   }

   // 选轴线（只允许 PB-Wall-Axis）
   var go = new Rhino.Input.Custom.GetObject();
   go.SetCommandPrompt("请点击需要开关封口的轴线端头（PB-Wall-Axis），Enter/Esc 取消");
   go.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
   go.SubObjectSelect = false;
   go.EnablePreSelect(true, true);
   go.EnablePostSelect(true);

   go.SetCustomGeometryFilter((rhObj, geo, ci) =>
   {
     if (rhObj == null) return false;
     if (!(geo is Rhino.Geometry.Curve)) return false;
     return IsAxisLayer(rhObj.Attributes.LayerIndex);
   });

   var r = go.Get();
   if (r != Rhino.Input.GetResult.Object || go.ObjectCount != 1) return;

   var axisRef = go.Object(0);
   var axisObj = axisRef?.Object();
   var axisCrv = axisRef?.Curve();

   if (axisObj == null || axisCrv == null || !axisCrv.IsValid) return;

   // 用被点轴线的 parentId 重新锁分区（更稳）
   {
     int li = axisObj.Attributes.LayerIndex;
     if (li >= 0 && li < doc.Layers.Count)
     {
       var lay = doc.Layers[li];
       if (lay != null) parentId = lay.ParentLayerId;
       if (parentId == Guid.Empty) parentId = lay.Id; // ★兜底
     }

   }

   // 判断更近的端点：Start / End
   Rhino.Geometry.Point3d pickPt = axisRef.SelectionPoint();
   var st = axisCrv.PointAtStart;
   var ed = axisCrv.PointAtEnd;

   bool atStart = pickPt.DistanceToSquared(st) <= pickPt.DistanceToSquared(ed);
   var axisEndPt = atStart ? st : ed;

   // 兼容两种 key：capston/capedon 或 capstarton/capendon（有哪个写哪个）
   string axisName = axisObj.Attributes.Name ?? "Wallaxis";

   bool hasCapStOnLong = axisName.IndexOf("|capstarton=", StringComparison.OrdinalIgnoreCase) >= 0;
   bool hasCapEdOnLong = axisName.IndexOf("|capendon=",   StringComparison.OrdinalIgnoreCase) >= 0;

    string keyStart = hasCapStOnLong ? "capstarton" : "capston";
    string keyEnd   = hasCapEdOnLong ? "capendon"   : "capedon";

    string keyThis  = atStart ? keyStart : keyEnd;

    int GetTag01(string name, string keyLower)
    {
     if (string.IsNullOrEmpty(name)) return 0;
     var parts = name.Split('|');
     foreach (var p in parts)
     {
       int eq = p.IndexOf('=');
       if (eq <= 0) continue;
       string k = p.Substring(0, eq).Trim();
       string v = p.Substring(eq + 1).Trim();
       if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase))
         return (v == "1") ? 1 : 0;
     }
     return 0;
   }

   string SetOrAppendTag01(string name, string keyLower, int v01)
   {
     if (string.IsNullOrEmpty(name)) name = "Wallaxis";
     var parts = new List<string>(name.Split('|'));
    bool replaced = false;

    for (int i = 0; i < parts.Count; i++)
    {
      int eq = parts[i].IndexOf('=');
      if (eq <= 0) continue;
      string k = parts[i].Substring(0, eq).Trim();
      if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase))
      {
        parts[i] = k + "=" + v01;
        replaced = true;
        break;
      }
    }

    if (!replaced) parts.Add(keyLower + "=" + v01);
    return string.Join("|", parts);
   }

   double BBDist(Rhino.Geometry.BoundingBox bb, Rhino.Geometry.Point3d p)
   {
    if (!bb.IsValid) return double.MaxValue;
    return bb.ClosestPoint(p).DistanceTo(p); // ✅ 你之前报错的 DistanceTo(BoundingBox) 用这个替代
   }

    // 找本分区下的墙图层
    var wallLayers = new List<Rhino.DocObjects.Layer>();
    for (int i = 0; i < doc.Layers.Count; i++)
    {
      var lay = doc.Layers[i];
     if (lay == null) continue;
     if (IsWallLayer(i)) wallLayers.Add(lay);
    }

    // 临时解锁这些墙层 + 轴线层（否则删不掉/加不上）
     var lockBackup = new Dictionary<int, bool>();
     void UnlockLayer(int li)
   {
      if (li < 0 || li >= doc.Layers.Count) return;
      var lay = doc.Layers[li];
      if (lay == null) return;
      if (!lockBackup.ContainsKey(li)) lockBackup[li] = lay.IsLocked;
      if (lay.IsLocked)
     {
       lay.IsLocked = false;
       doc.Layers.Modify(lay, li, true);
     }
   }
      void RestoreLocks()
   {
     foreach (var kv in lockBackup)
     {
      int li = kv.Key;
      bool wasLocked = kv.Value;
      if (li < 0 || li >= doc.Layers.Count) continue;
      var lay = doc.Layers[li];
      if (lay == null) continue;
      lay.IsLocked = wasLocked;
      doc.Layers.Modify(lay, li, true);
     }
   }

    UnlockLayer(axisObj.Attributes.LayerIndex);
     foreach (var lay in wallLayers) UnlockLayer(lay.Index);

     try
   {
    Guid axisId = axisObj.Id;
    string axisIdStr = axisId.ToString();

    int capOn = GetTag01(axisName, keyThis);

    // 1) 先找“这个端头”的 cap
    Rhino.DocObjects.RhinoObject capObjToDelete = null;

    double radius = 1000.0; // 你的粗筛半径
    foreach (var lay in wallLayers)
    {
      var objs = doc.Objects.FindByLayer(lay);
      if (objs == null) continue;

      foreach (var o in objs)
      {
        if (o == null) continue;
        var n = o.Attributes.Name ?? "";

        // 只认 Wallcap（你后续也可以加更多兼容规则）
        if (!n.StartsWith("Wallcap", StringComparison.OrdinalIgnoreCase)) continue;
        if (n.IndexOf("AxisId=" + axisIdStr, StringComparison.OrdinalIgnoreCase) < 0) continue;

        // 端头标记：At=st/ed（你现成 cap 若用 Cap=Start/End，也可以在这里再加一条 contains 判断）
        string atNeed = atStart ? "At=st" : "At=ed";
        if (n.IndexOf(atNeed, StringComparison.OrdinalIgnoreCase) < 0) continue;

        var c = o.Geometry as Rhino.Geometry.Curve;
        if (c == null) continue;

        var bb = c.GetBoundingBox(true);
        if (BBDist(bb, axisEndPt) > radius) continue;

        capObjToDelete = o;
        break;
      }
      if (capObjToDelete != null) break;
    }

    // 2) capOn==1：删 cap + 轴线 tag 置 0
    if (capOn == 1)
    {
      if (capObjToDelete != null)
      {
        doc.Objects.Delete(capObjToDelete, true);
        RhinoApp.WriteLine("[Cap] 已删除封口。");
      }
      else
      {
        RhinoApp.WriteLine("[Cap] 轴线标记为有封口，但没找到对应 cap，已强制同步为无封口。");
      }

      var aAttr = axisObj.Attributes;
      aAttr.Name = SetOrAppendTag01(axisName, keyThis, 0);
      doc.Objects.ModifyAttributes(axisObj, aAttr, true);
      doc.Views.Redraw();
      return;
    }

    // 3) capOn==0：创建 cap（找同 AxisId 的 Left/Right 墙线）
    Rhino.Geometry.Curve wallL = null, wallR = null;
    int wallLayerIndexForCap = -1;

    foreach (var lay in wallLayers)
    {
      var objs = doc.Objects.FindByLayer(lay);
      if (objs == null) continue;

      foreach (var o in objs)
      {
        if (o == null) continue;
        var n = o.Attributes.Name ?? "";
        if (!n.StartsWith("Wall|", StringComparison.OrdinalIgnoreCase)) continue;
        if (n.IndexOf("AxisId=" + axisIdStr, StringComparison.OrdinalIgnoreCase) < 0) continue;

        // 排除 cap 自己（如果你 cap 是用 Wall|...|Cap=...，这里就能挡住）
        if (n.IndexOf("|Cap=", StringComparison.OrdinalIgnoreCase) >= 0) continue;

        var c = o.Geometry as Rhino.Geometry.Curve;
        if (c == null || !c.IsValid) continue;

        if (n.IndexOf("|Side=Left", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          wallL = c.DuplicateCurve();
          wallLayerIndexForCap = lay.Index;
        }
        else if (n.IndexOf("|Side=Right", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          wallR = c.DuplicateCurve();
          wallLayerIndexForCap = lay.Index;
        }
      }
    }

    if (wallL == null || wallR == null)
    {
      RhinoApp.WriteLine("[Cap] 没找到这根轴线的左右墙线，无法创建封口。");
      return;
    }

    // ★ 1.3.2：封口端点严格对应轴线端点：轴线 Start → 墙线 Start；轴线 End → 墙线 End
    Rhino.Geometry.Point3d pl = atStart ? wallL.PointAtStart : wallL.PointAtEnd;
    Rhino.Geometry.Point3d pr = atStart ? wallR.PointAtStart : wallR.PointAtEnd;

    var capCrv = new Rhino.Geometry.LineCurve(pl, pr);

    // 生成 cap：Name 统一 Wallcap|AxisId=...|At=st/ed
    var capAttr = new Rhino.DocObjects.ObjectAttributes();
    capAttr.LayerIndex = wallLayerIndexForCap;
    capAttr.PlotWeightSource = Rhino.DocObjects.ObjectPlotWeightSource.PlotWeightFromLayer;

    capAttr.Name =
      "Wallcap"
      + "|AxisId=" + axisIdStr
      + (atStart ? "|At=st" : "|At=ed");

    doc.Objects.AddCurve(capCrv, capAttr);

    // 更新轴线 tag = 1
    {
      var aAttr = axisObj.Attributes;
      aAttr.Name = SetOrAppendTag01(axisName, keyThis, 1);
      doc.Objects.ModifyAttributes(axisObj, aAttr, true);
    }

    RhinoApp.WriteLine("[Cap] 已创建封口。");
    doc.Views.Redraw();
   }
   finally
   {
    RestoreLocks();
   }
 }
}

Main();
