using System.Collections.Generic;

/// <summary>
/// 跨线程安全的原始输入事件队列。
/// 低层鼠标钩子（WH_MOUSE_LL，运行在独立线程）把屏幕坐标换算后的指针/按键事件入队，
/// 由 WallpaperInputModule 在每帧 EventSystem.Process 前消费，喂给 UGUI。
///
/// 关键优化：连续的“移动”事件会被合并为最近一次（仅保留最新位置），
/// 避免每帧处理成百上千条移动并反复执行 raycast，从而消除鼠标卡顿。
/// </summary>
public class WallpaperClickQueue
{
    public struct RawClick
    {
        public int kind;        // 0=移动 1=按下 2=抬起 3=滚轮 4=横向滚轮 5=强制释放
        public int px, py;      // 客户区像素坐标（Y 向下，与 Unity 屏幕坐标一致）
        public int button;      // 0=左 1=右 2=中
        public float wheel;     // 滚轮增量（以“格”为单位）
    }

    private readonly object m_Lock = new object();
    private readonly List<RawClick> m_List = new List<RawClick>();

    public void EnqueueMove(int px, int py)
    {
        lock (m_Lock)
        {
            // 合并连续移动：若队尾已是移动事件，直接更新其坐标，不新增
            if (m_List.Count > 0 && m_List[m_List.Count - 1].kind == 0)
            {
                RawClick last = m_List[m_List.Count - 1];
                last.px = px; last.py = py;
                m_List[m_List.Count - 1] = last;
            }
            else
            {
                m_List.Add(new RawClick { kind = 0, px = px, py = py });
            }
        }
    }

    public void EnqueueButton(int button, bool down)
    {
        lock (m_Lock) m_List.Add(new RawClick { kind = down ? 1 : 2, button = button });
    }

    public void EnqueueWheel(float wheel)
    {
        lock (m_Lock) m_List.Add(new RawClick { kind = 3, wheel = wheel });
    }

    public void EnqueueWheelH(float wheel)
    {
        lock (m_Lock) m_List.Add(new RawClick { kind = 4, wheel = wheel });
    }

    public void EnqueueForceRelease()
    {
        lock (m_Lock) m_List.Add(new RawClick { kind = 5 });
    }

    public int Count
    {
        get { lock (m_Lock) return m_List.Count; }
    }

    /// <summary>从队列取出一个事件（非阻塞）。没有则返回 false。</summary>
    public bool TryDequeue(out RawClick item)
    {
        lock (m_Lock)
        {
            if (m_List.Count > 0) { item = m_List[0]; m_List.RemoveAt(0); return true; }
        }
        item = default(RawClick);
        return false;
    }
}
