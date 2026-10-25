using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// 壁纸演示 / 调试 UI（从 WallpaperController 抽离的“UI 功能代码”）。
///
/// 职责：
///   1. 演示按钮点击计数（直观验证“按钮在壁纸态可点”）；
///   2. 屏幕角落的调试信息文本面板（钩子状态 / 输入模块计数 / 队列剩余）；
///   3. 装饰层 raycast 穿透（被遮挡也能点到按钮的修复，当前默认不启用）。
///
/// 它只【观察】壁纸引擎（WallpaperController / WallpaperInputModule）暴露的只读计数，
/// 不含任何 Win32 / 桌面挂接逻辑。由 WallpaperController.EnsureDemoUI() 自动挂载并 Init(this)。
/// </summary>
public class WallpaperDemoUI : MonoBehaviour
{
    private WallpaperInputModule m_InputModule;

    private Text m_StatusText;
    private Button m_DemoButton;
    private int m_ClickCount;
    private float m_DebugTimer;

    public void Awake()
    {
        try
        {
            SetupDemoUI();
        }
        catch (System.Exception e)
        {
            Debug.Log("警告: 演示 UI 装配失败 " + e.Message);
        }
        try
        {
            SetupDebugText();
        }
        catch (System.Exception e)
        {
            Debug.Log("警告: 调试文本创建失败 " + e.Message);
        }
    }

    /// <summary>演示按钮：找到场景中名为 Button 的对象并挂点击监听。</summary>
    void SetupDemoUI()
    {
        GameObject btn = GameObject.Find("Button");
        if (btn == null)
        {
            Debug.Log("警告: 未找到名为 Button 的对象"); return;
        }

        m_DemoButton = btn.GetComponent<Button>();
        if (m_DemoButton == null)
        {
            Debug.Log("警告: Button 上缺少 Button 组件"); return;
        }
        m_DemoButton.onClick.AddListener(OnDemoButtonClicked);
    }

    void OnDemoButtonClicked()
    {
        m_ClickCount++;
    }

    /// <summary>
    /// 让纯装饰性 UI 不参与 raycast。
    /// 场景里的 Text 完全遮挡 Button 时，会把点击吃掉（点击永不触发）。
    /// 装饰层设为 raycastTarget=false 后，射线可穿透到 Button。
    ///
    /// 注意：必须排除所有【交互控件】（Button / InputField / Selectable），
    /// 否则会连带破坏输入框等本来可用的控件。
    ///
    /// 当前默认不启用（与改造前一致），保留为可选优化。
    /// </summary>
    void DisableDecorativeRaycast()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null) return;
        foreach (var g in canvas.GetComponentsInChildren<Graphic>(true))
        {
            if (g == null) continue;
            var go = g.gameObject;
            if (go.GetComponent<Selectable>() != null) continue;
            g.raycastTarget = false;
        }
        Debug.Log("装饰层 raycast 已关闭（交互控件保留）");
    }

    void SetupDebugText()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            Debug.Log("警告: 未找到 Canvas，调试文本不显示"); return;
        }

        GameObject go = new GameObject("WallpaperDebugText");
        go.transform.SetParent(canvas.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(12f, -12f);
        rt.sizeDelta = new Vector2(460f, 150f);

        // 底板必须是【独立子节点】：Image 与 Text 都是 Graphic 子类，
        // Unity 限制「一个 GameObject 只能有一个 Graphic 组件」，
        // 同一节点上加 Image 再 AddComponent<Text> 会抛异常并中断 Start()（挂载全部失效）。
        GameObject bgGo = new GameObject("DebugBackground");
        bgGo.transform.SetParent(go.transform, false);
        RectTransform bgRt = bgGo.AddComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        Image bg = bgGo.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.62f);
        bg.raycastTarget = false;   // 底板不参与 raycast，避免干扰 UI 命中

        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = 22;
        t.color = Color.white;
        t.alignment = TextAnchor.UpperLeft;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;     // 调试文本不参与 raycast

        // 描边（Outline 继承 BaseMeshEffect，不是 Graphic，可与 Text 共存）
        Outline o = go.AddComponent<Outline>();
        o.effectColor = new Color(0f, 0f, 0f, 1f);
        o.effectDistance = new Vector2(1.5f, -1.5f);

        t.text = "壁纸就绪";
        m_StatusText = t;
    }

    void Update()
    {
        // 周期刷新调试文本（0.5s 节流，避免每帧重建字符串）
        m_DebugTimer += Time.deltaTime;
        if (m_DebugTimer < 0.5f) return;
        m_DebugTimer = 0f;

        if (m_StatusText == null) return;

        // 懒加载输入模块（编辑器中可能未挂 WallpaperInputModule）
        if (m_InputModule == null)
        {
            EventSystem es = EventSystem.current ?? FindObjectOfType<EventSystem>();
            if (es != null) m_InputModule = es.GetComponent<WallpaperInputModule>();
        }

        string hook = WallpaperController.IsHookInstalled()
            ? "鼠标钩子: 已启用" : "鼠标钩子: 未启用";
        string raycast = (m_InputModule != null)
            ? "\n按下:" + m_InputModule.PressCount + " 抬起:" + m_InputModule.ReleaseCount
              + "\n命中:" + m_InputModule.RaycastHitCount + " 点击:" + m_InputModule.ClickCount
              + "\n命中物:" + m_InputModule.LastHitName
            : "";
        m_StatusText.text = hook
            + "\n按钮点击次数: " + m_ClickCount
            + "\n已喂事件 移动:" + WallpaperController.FedMoves + " 按下:" + WallpaperController.FedDowns

            + raycast;
    }
}
