using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 替代默认 StandaloneInputModule：直接消费低层鼠标钩子队列，把“壁纸窗口背后的桌面鼠标”
/// 喂给 UGUI，使按钮点击 / 拖拽在窗口失焦（壁纸态）也能正常响应。
///
/// 关键：不依赖 input 属性。该属性在当前 Unity 版本为只读，IL2CPP 构建下反射私有
/// 字段 m_Input 也会失败 —— 那是上一版“按钮不可点”的根因。本类完全接管鼠标处理：
/// 移动 / 拖拽复用框架受保护的 ProcessMove / ProcessDrag；按下 / 抬起 / 点击则直接
/// 用 ExecuteEvents 派发（Unity 2021.3 没有 ProcessDown / ProcessUp 这类方法）。
/// </summary>
public class WallpaperInputModule : StandaloneInputModule
{
    private WallpaperClickQueue m_Queue;
    private readonly Dictionary<int, PointerEventData> m_Pointers = new Dictionary<int, PointerEventData>();
    private readonly List<RaycastResult> m_RaycastResults = new List<RaycastResult>();
    private Vector2 m_LastPos = Vector2.zero;
    private bool m_HasLast;

    // 诊断计数（显示在调试文本上）
    public int PressCount;      // 收到的按下事件数
    public int ReleaseCount;    // 收到的抬起事件数
    public int ClickCount;      // 实际触发的点击数
    public int RaycastHitCount; // raycast 命中的元素数
    public string LastHitName = "无";

    public void BindQueue(WallpaperClickQueue queue) { m_Queue = queue; }

    /// <summary>
    /// 关键：UGUI 的 raycast 原本在 GetMousePointerEventData() 里执行（它调 eventSystem.RaycastAll）。
    /// 本类 override 了 Process()，完全绕过了它 → pointerCurrentRaycast 永远是空的 →
    /// 无论点多少次都命中不到任何 UI（表现为“按钮完全没反应”）。
    /// 因此必须在此手动 raycast。
    /// </summary>
    private void RaycastAt(PointerEventData p)
    {
        m_RaycastResults.Clear();
        eventSystem.RaycastAll(p, m_RaycastResults);
        p.pointerCurrentRaycast = m_RaycastResults.Count > 0
            ? m_RaycastResults[0]
            : new RaycastResult();
    }

    /// <summary>
    /// 【核心修复】穿透遮挡查找真正的点击目标。
    ///
    /// 问题：场景里 Button 被同层/上层的 Text、Image 等 Graphic 覆盖时，
    /// `m_RaycastResults[0]` 命中的是**最上层**对象（如 Text），
    /// 而 Text 自己不实现 IPointerClickHandler、也不在 Button 的父链上
    /// → `GetEventHandler&lt;IPointerClickHandler&gt;(currentOverGo)` 返回 null
    /// → `pointerPress != pointerUpHandler` → 点击【永远不触发】（日志实测 upHandler=无）。
    ///
    /// 解法：`GraphicRaycaster.RaycastAll` 返回的是**射线路径上全部** graphic（含被遮挡者），
    /// 逐个向上解析（ExecuteHierarchy），取第一个能响应 handler 的对象。
    /// 这与「被遮挡也能点到」的实际诉求一致。
    /// </summary>
    private GameObject FindClickableTarget(PointerEventData p)
    {
        // 复用 Process() 里已完成的 raycast 结果（m_RaycastResults 是射线路径上的全量列表），
        // 避免重复 RaycastAll。结果为空时补一次。
        if (m_RaycastResults.Count == 0)
        {
            m_RaycastResults.Clear();
            eventSystem.RaycastAll(p, m_RaycastResults);
        }

        // 逐个向上解析（ExecuteHierarchy），取第一个能响应点击的对象。
        // 列表按距离从近到远排序，因此「第一个可点击者」即最上层可点击者。
        for (int i = 0; i < m_RaycastResults.Count; i++)
        {
            var go = m_RaycastResults[i].gameObject;
            if (go == null) continue;
            var h = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
            if (h != null) return h;
        }
        return null;
    }

    /// <summary>当前 raycast 命中列表的可读描述（用于诊断遮挡）。</summary>
    private string DescribeRaycastList()
    {
        if (m_RaycastResults.Count == 0) return "(空)";
        var sb = new StringBuilder(128);
        int n = Mathf.Min(m_RaycastResults.Count, 6);
        for (int i = 0; i < n; i++)
        {
            var go = m_RaycastResults[i].gameObject;
            if (i > 0) sb.Append(" > ");
            sb.Append(go != null ? go.name : "?");
        }
        if (m_RaycastResults.Count > n) sb.Append(" …共").Append(m_RaycastResults.Count);
        return sb.ToString();
    }

    /// <summary>
    /// 取得「最上层」命中对象的名称（用于诊断文本，判断遮挡情况）。
    /// </summary>
    private string DescribeTopHit(PointerEventData p)
    {
        var go = p.pointerCurrentRaycast.gameObject;
        return go != null ? go.name : "无";
    }

    public override void Process()
    {
        // 队列为空（如编辑器下未绑定）时退回默认处理，避免破坏其它输入
        if (m_Queue == null) { base.Process(); return; }

        WallpaperClickQueue.RawClick ev;
        while (m_Queue.TryDequeue(out ev))
        {
            switch (ev.kind)
            {
                case 0: // 移动：更新所有指针位置 + hover/拖拽
                {
                    // 直接用客户区像素坐标（Y 向下），与 Unity Screen 坐标一致。
                    // GraphicRaycaster 内部走 ScreenToViewportPoint(eventPosition)，
                    // 因此必须传真实屏幕像素坐标，不能传归一化值。
                    Vector2 pos = new Vector2(ev.px, ev.py);
                    m_HasLast = true;
                    foreach (var kv in m_Pointers)
                    {
                        PointerEventData p = kv.Value;
                        p.delta = pos - p.position;
                        p.position = pos;
                        RaycastAt(p);      // ← 必须先 raycast，再 ProcessMove/ProcessDrag
                        ProcessMove(p);
                        ProcessDrag(p);
                    }
                    m_LastPos = pos;
                    break;
                }
                case 1: // 按下
                case 2: // 抬起
                {
                    int pid = ButtonToPointerId(ev.button);
                    PointerEventData p = GetPointer(pid);
                    if (m_HasLast) { p.delta = m_LastPos - p.position; p.position = m_LastPos; }
                    p.button = (PointerEventData.InputButton)ev.button;
                    RaycastAt(p);          // ← 必须先 raycast，保证 down/up 命中正确
                    ProcessMove(p);
                    if (ev.kind == 1) ProcessPress(p);
                    else ProcessRelease(p);
                    break;
                }
                case 3: // 滚轮
                case 4: // 横向滚轮
                {
                    PointerEventData p = GetPointer(-1);
                    if (m_HasLast) { p.delta = Vector2.zero; p.position = m_LastPos; }
                    p.scrollDelta = (ev.kind == 3)
                        ? new Vector2(0f, ev.wheel)
                        : new Vector2(ev.wheel, 0f);
                    RaycastAt(p);
                    ProcessMove(p);
                    if (p.pointerCurrentRaycast.gameObject != null)
                    {
                        var scrollGO = ExecuteEvents.GetEventHandler<IScrollHandler>(
                            p.pointerCurrentRaycast.gameObject);
                        if (scrollGO != null)
                            ExecuteEvents.ExecuteHierarchy(scrollGO, p, ExecuteEvents.scrollHandler);
                    }
                    p.scrollDelta = Vector2.zero;
                    break;
                }
                case 5: // 强制释放（指针离开壁纸区域 / 退出，防止卡键）
                {
                    ForceReleaseAll();
                    break;
                }
            }
        }
    }

    private PointerEventData GetPointer(int pointerId)
    {
        if (!m_Pointers.TryGetValue(pointerId, out var p))
        {
            p = new PointerEventData(eventSystem);
            p.pointerId = pointerId;
            m_Pointers[pointerId] = p;
        }
        return p;
    }

    /// <summary>
    /// 按下派发（严格对齐 Unity 的 ProcessMousePress 的 PressedThisFrame 分支）。
    /// 必须在调用前用 ProcessMove 刷新 pointerCurrentRaycast。
    /// </summary>
    private void ProcessPress(PointerEventData pointer)
    {
        var currentOverGo = pointer.pointerCurrentRaycast.gameObject;

        // 诊断：记录 raycast 命中情况
        PressCount++;
        if (currentOverGo != null)
        {
            RaycastHitCount++;
            LastHitName = currentOverGo.name;
        }
        WallpaperDebugLog.Diag("按下 id=" + pointer.pointerId + " pos=" + (int)pointer.position.x + "," + (int)pointer.position.y
            + " 命中=" + DescribeTopHit(pointer)
            + " 射线列表=" + DescribeRaycastList());

        pointer.pressPosition = pointer.position;
        pointer.pointerPressRaycast = pointer.pointerCurrentRaycast;
        pointer.eligibleForClick = true;
        pointer.delta = Vector2.zero;
        pointer.dragging = false;
        pointer.useDragThreshold = true;

        // 穿透遮挡：取「能响应点击」的目标（不一定是 raycast 最上层对象）
        var clickable = FindClickableTarget(pointer);
        if (clickable != null)
        {
            pointer.pointerPress = clickable;

            // 初始化潜在拖拽，并记下拖拽目标
            GameObject newPressed = ExecuteEvents.ExecuteHierarchy(
                clickable, pointer, ExecuteEvents.initializePotentialDrag);
            if (newPressed != null) pointer.pointerDrag = newPressed;

            WallpaperDebugLog.Diag("  按下解析到可点击对象=" + clickable.name);
        }
        else if (currentOverGo != null)
        {
            // 无 handler 时退化为最上层命中对象（保持 Unity 兼容语义）
            pointer.pointerPress = currentOverGo;
        }
    }

    /// <summary>
    /// 抬起派发（严格对齐 Unity 的 ReleaseMouse）。
    /// 必须在调用前用 ProcessMove 刷新 pointerCurrentRaycast。
    /// </summary>
    private void ProcessRelease(PointerEventData pointer)
    {
        var currentOverGo = pointer.pointerCurrentRaycast.gameObject;
        ReleaseCount++;

        // 无论是否有 pointerPress，都要尝试派发 Up（Unity 语义）
        if (pointer.pointerPress != null)
        {
            ExecuteEvents.ExecuteHierarchy(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
        }

        // 点击判定：与 ProcessPress 使用【同一套】穿透遮挡解析规则，
        // 保证 pointerPress == pointerUpHandler 成立（否则点击永不触发）。
        var clickable = FindClickableTarget(pointer);
        WallpaperDebugLog.Diag("抬起 命中=" + (currentOverGo != null ? currentOverGo.name : "无")
            + " press=" + (pointer.pointerPress != null ? pointer.pointerPress.name : "无")
            + " upHandler=" + (clickable != null ? clickable.name : "无")
            + " eligible=" + pointer.eligibleForClick);

        if (clickable != null && pointer.pointerPress == clickable && pointer.eligibleForClick)
        {
            ClickCount++;
            WallpaperDebugLog.Write("触发点击: " + pointer.pointerPress.name
                + " (命中=" + (currentOverGo != null ? currentOverGo.name : "无") + ")");
            ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerClickHandler);
        }

        // 结束拖拽
        if (pointer.pointerDrag != null && pointer.dragging)
        {
            ExecuteEvents.ExecuteHierarchy(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler);
        }

        pointer.eligibleForClick = false;
        pointer.pointerPress = null;
        pointer.pointerDrag = null;
        pointer.dragging = false;
        pointer.rawPointerPress = null;
    }

    /// <summary>把所有仍按下的按键抬起，避免“卡键”。</summary>
    private void ForceReleaseAll()
    {
        foreach (var kv in m_Pointers)
        {
            PointerEventData p = kv.Value;
            if (m_HasLast) { p.delta = Vector2.zero; p.position = m_LastPos; }
            RaycastAt(p);          // ← 必须先 raycast
            ProcessMove(p);
            ProcessRelease(p);
        }
        m_HasLast = false;
    }

    /// <summary>与 StandaloneInputModule 一致的鼠标 pointerId：左 -1 / 右 -2 / 中 -3。</summary>
    private static int ButtonToPointerId(int button)
    {
        switch (button)
        {
            case 0: return -1;
            case 1: return -2;
            case 2: return -3;
            default: return -1;
        }
    }
}
