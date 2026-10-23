using System.Collections.Generic;
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
    private Vector2 m_LastPos = Vector2.zero;
    private bool m_HasLast;

    public void BindQueue(WallpaperClickQueue queue) { m_Queue = queue; }

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
                    Vector2 pos = new Vector2(ev.nx * Screen.width, ev.ny * Screen.height);
                    m_HasLast = true;
                    foreach (var kv in m_Pointers)
                    {
                        PointerEventData p = kv.Value;
                        p.delta = pos - p.position;
                        p.position = pos;
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
                    ProcessMove(p); // 先 raycast，保证 down/up 命中正确
                    p.button = (PointerEventData.InputButton)ev.button;
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
    /// 按下派发（等价于 Unity 内部 ProcessMousePress 的 PressedThisFrame 分支）。
    /// 必须在调用前用 ProcessMove 刷新 pointerCurrentRaycast。
    /// </summary>
    private void ProcessPress(PointerEventData pointer)
    {
        var currentOverGo = pointer.pointerCurrentRaycast.gameObject;

        pointer.pressPosition = pointer.position;
        pointer.pointerPressRaycast = pointer.pointerCurrentRaycast;
        pointer.eligibleForClick = true;
        pointer.delta = Vector2.zero;
        pointer.dragging = false;
        pointer.useDragThreshold = true;

        if (currentOverGo != null)
        {
            ExecuteEvents.ExecuteHierarchy(currentOverGo, pointer, ExecuteEvents.pointerDownHandler);
            // 初始化潜在拖拽，并记下拖拽目标
            GameObject newPressed = ExecuteEvents.ExecuteHierarchy(
                currentOverGo, pointer, ExecuteEvents.initializePotentialDrag);
            if (newPressed != null) pointer.pointerDrag = newPressed;
        }
        pointer.pointerPress = currentOverGo;
    }

    /// <summary>
    /// 抬起派发（等价于 Unity 内部 ProcessMousePress 的 ReleasedThisFrame 分支）。
    /// 必须在调用前用 ProcessMove 刷新 pointerCurrentRaycast。
    /// </summary>
    private void ProcessRelease(PointerEventData pointer)
    {
        var currentOverGo = pointer.pointerCurrentRaycast.gameObject;
        var go = pointer.pointerPress;

        if (go != null)
        {
            ExecuteEvents.ExecuteHierarchy(go, pointer, ExecuteEvents.pointerUpHandler);

            // 若抬起时仍停在同一可点击元素上，触发点击
            GameObject pointerUpHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            if (pointer.pointerPress == pointerUpHandler && pointer.eligibleForClick)
            {
                ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerClickHandler);
            }

            // 结束拖拽
            if (pointer.pointerDrag != null && pointer.dragging)
            {
                ExecuteEvents.ExecuteHierarchy(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler);
            }
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
