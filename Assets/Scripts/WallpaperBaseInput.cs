using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 自定义 BaseInput：把“来自低层鼠标钩子的假输入”暴露给 StandaloneInputModule，
/// 从而让 UGUI 的按钮/拖拽在“窗口是壁纸、收不到真实鼠标消息”时也能正常响应。
///
/// 关键：mousePresent 恒为 true，绕开 StandaloneInputModule 的失焦门禁
/// （默认 !isFocused && !mousePresent 时直接 return，导致按钮点击完全失效）。
/// </summary>
public class WallpaperBaseInput : BaseInput
{
    private Vector2 m_Position = new Vector2(0f, 0f);
    private readonly bool[] m_Held = new bool[3];
    private readonly bool[] m_Pressed = new bool[3];
    private readonly bool[] m_Released = new bool[3];
    private Vector2 m_Scroll = Vector2.zero;

    public override Vector2 mousePosition { get { return m_Position; } }
    public override bool mousePresent { get { return true; } }
    public override Vector2 mouseScrollDelta { get { return m_Scroll; } }

    public override bool GetMouseButton(int button)
    {
        return button >= 0 && button < 3 && m_Held[button];
    }

    public override bool GetMouseButtonDown(int button)
    {
        return button >= 0 && button < 3 && m_Pressed[button];
    }

    public override bool GetMouseButtonUp(int button)
    {
        return button >= 0 && button < 3 && m_Released[button];
    }

    // ---- 由钩子/队列写入 ----
    public void SetPosition(float x, float y) { m_Position.x = x; m_Position.y = y; }
    public void Press(int button) { if (button < 0 || button > 2) return; m_Held[button] = true; m_Pressed[button] = true; }
    public void Release(int button) { if (button < 0 || button > 2) return; m_Held[button] = false; m_Released[button] = true; }
    public void AddScroll(float x, float y) { m_Scroll.x += x; m_Scroll.y += y; }

    /// <summary>每帧 Process 结束后调用：清除“按下/抬起”边沿，避免被重复处理。</summary>
    public void EndFrame()
    {
        for (int i = 0; i < 3; i++) { m_Pressed[i] = false; m_Released[i] = false; }
        m_Scroll = Vector2.zero;
    }

    /// <summary>强制复位所有按键（指针离开壁纸区域 / 退出时调用），防止卡键。</summary>
    public void ForceReleaseAll()
    {
        for (int i = 0; i < 3; i++) { m_Held[i] = false; m_Pressed[i] = false; m_Released[i] = false; }
    }
}
