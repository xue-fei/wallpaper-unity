using System.Collections;
using UnityEngine;

public class Effect1 : MonoBehaviour
{
    public Material m;
    private Coroutine quitCoroutine;

    // Use this for initialization
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }

    void OnMouseEnter()
    {
        if (m.color != Color.blue)
        {
            m.color = Color.blue;
        }
        // 如果已经有倒计时在运行，先取消（防止重复触发）
        if (quitCoroutine != null)
        {
            StopCoroutine(quitCoroutine);
        }

        // 启动新的 3 秒倒计时
        quitCoroutine = StartCoroutine(QuitAfterDelay(3f));
    }

    void OnMouseExit()
    {
        if (m.color != Color.green)
        {
            m.color = Color.green;
        }
        // 鼠标离开，取消倒计时
        if (quitCoroutine != null)
        {
            StopCoroutine(quitCoroutine);
            quitCoroutine = null;
        }
    }

    IEnumerator QuitAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        // 倒计时完成，退出程序
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif

        // 清理引用（可选）
        quitCoroutine = null;
    }
}