using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Player: run/jump, a limited light that shrinks as you throw it, and touching things (tags):
/// Shard = +light, Finish = exit, Enemy = restart the level.
/// The light is a child Sprite Mask that cuts a hole in the "Darkness" sprite; its size is the light radius,
/// and it shifts ahead in the direction you're moving.
/// Controls: A/D move, Space jump, Left-click throw light at the cursor, R restart.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class Player : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 7f;
    public float jumpVelocity = 13f;
    public LayerMask groundMask;

    [Header("Light")]
    public Transform lightMask;       // child with a circle Sprite Mask
    public float maxLight = 100f;
    public float lightAmount = 100f;
    public float throwCost = 15f;
    public float maxRadius = 4f;
    public float minRadius = 1.6f;
    public float darkRadius = 0.8f;   // small glow left when the light is used up
    [Tooltip("How far the light shifts ahead in the direction you're moving, as a fraction of its radius.")]
    [Range(0f, 0.8f)] public float lightLead = 0.35f;

    [Header("Throwing")]
    [Tooltip("Disabled LightOrb object in the scene; each throw clones it.")]
    public Rigidbody2D orbTemplate;
    public float throwSpeed = 15f;

    public bool HasLight => lightAmount > 0f;
    public float LightRadius => lightMask != null ? lightMask.localScale.x * 0.5f : 0f;
    /// <summary>Centre of the light circle (it leads ahead of the player). Enemies notice you from here.</summary>
    public Vector2 LightCenter => lightMask != null ? (Vector2)lightMask.position : (Vector2)transform.position;

    Rigidbody2D _rb;
    Collider2D _col;
    bool _won;
    int _facing = 1;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _col = GetComponent<Collider2D>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb != null && kb.rKey.wasPressedThisFrame) Restart();
        if (_won || kb == null) return;

        // run + jump
        float x = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
        Vector2 v = _rb.linearVelocity;
        v.x = x * moveSpeed;
        bool grounded = Physics2D.OverlapBox((Vector2)_col.bounds.center + Vector2.down * _col.bounds.extents.y, new Vector2(_col.bounds.size.x * 0.9f, 0.1f), 0f, groundMask);
        if (grounded && (kb.spaceKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame)) v.y = jumpVelocity;
        if (kb.spaceKey.wasReleasedThisFrame && v.y > 0f) v.y *= 0.5f; // short hop
        _rb.linearVelocity = v;

        // throw light toward the mouse
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && HasLight)
        {
            Vector2 aim = ((Vector2)Camera.main.ScreenToWorldPoint(mouse.position.ReadValue()) - (Vector2)transform.position).normalized;
            var orb = Instantiate(orbTemplate, transform.position + (Vector3)(aim * 0.6f), Quaternion.identity);
            orb.gameObject.SetActive(true);
            Physics2D.IgnoreCollision(orb.GetComponent<Collider2D>(), _col);
            orb.linearVelocity = aim * throwSpeed;
            lightAmount = Mathf.Max(0f, lightAmount - throwCost);
        }

        // light radius follows the reserve
        float target = HasLight ? Mathf.Lerp(minRadius, maxRadius, lightAmount / maxLight) : darkRadius;
        float r = Mathf.Lerp(LightRadius, target, Time.deltaTime * 6f);
        lightMask.localScale = new Vector3(r * 2f, r * 2f, 1f);

        // light leads ahead in the direction you're moving (and stays facing that way when you stop)
        if (x != 0f) _facing = x > 0f ? 1 : -1;
        Vector3 lead = new Vector3(_facing * r * lightLead, 0f, 0f);
        lightMask.localPosition = Vector3.Lerp(lightMask.localPosition, lead, Time.deltaTime * 5f);

        if (transform.position.y < -4f) Restart();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("Shard")) { lightAmount = Mathf.Min(maxLight, lightAmount + 40f); Destroy(other.gameObject); }
        else if (other.CompareTag("Finish")) { _won = true; _rb.linearVelocity = Vector2.zero; }
        else if (other.CompareTag("Enemy")) Restart();
    }

    // Dying (enemy or falling) simply restarts the level.
    void Restart() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

    void OnGUI()
    {
        GUI.Label(new Rect(20, 20, 400, 30), $"Light: {Mathf.CeilToInt(lightAmount)}");
        if (_won) GUI.Label(new Rect(20, 45, 400, 30), "You escaped! Press R to play again.");
        else if (!HasLight) GUI.Label(new Rect(20, 45, 400, 30), "Out of light. Find a shard, or press R.");
    }
}
