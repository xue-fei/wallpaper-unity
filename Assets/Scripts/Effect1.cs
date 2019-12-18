using UnityEngine;

public class Effect1 : MonoBehaviour
{
    public Material m;
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
    }

    void OnMouseExit()
    {
        if (m.color != Color.green)
        {
            m.color = Color.green;
        }
    }
}