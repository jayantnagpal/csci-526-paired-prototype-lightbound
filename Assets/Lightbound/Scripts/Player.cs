using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Player: run/jump, a limited light that shrinks as you throw it, and touching things (tags):
/// Shard = +light, Finish = exit, Enemy = respawn. Shadow walls (barriers) break under thrown light, and you can pass them at 0 light.
/// Checkpoints light up after absorbing 2 thrown orbs, then become the respawn point (and refill light).
/// The light is a child soft Point Light 2D (the world is lit only by a dim Global Light 2D) and its radius shrinks with the reserve,
/// It also shifts ahead in the direction you're moving.
/// Controls: A/D move, Space jump, Left-click throw light at the cursor, R respawn at checkpoint, Shift+R restart level.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class Player : MonoBehaviour
{
    /// <summary>Inner radius as a fraction of the outer one. Low = soft glow that fades out, ~1 = hard circle.</summary>
    public const float GlowInner = 0.3f;

    [Header("Movement")]
    public float moveSpeed = 7f;
    public float jumpVelocity = 13f;
    public LayerMask groundMask;

    [Header("Light")]
    public Transform lightMask;       // child with a soft Point Light 2D
    public float maxLight = 100f;
    public float lightAmount = 100f;
    public float throwCost = 15f;
    public float maxRadius = 4.5f;
    public float minRadius = 1.6f;
    public float darkRadius = 0.8f;   // small glow left when the light is used up
    [Tooltip("How far the light shifts ahead in the direction you're moving, as a fraction of its radius.")]
    [Range(0f, 0.8f)] public float lightLead = 0.35f;

    [Header("Throwing")]
    [Tooltip("Disabled LightOrb object in the scene; each throw clones it.")]
    public Rigidbody2D orbTemplate;
    public float throwSpeed = 15f;

    [Header("Aim")]
    [Tooltip("Dotted line showing where a thrown orb will fly (it follows the orb's gravity arc).")]
    public bool showAim = true;
    public float aimLength = 10f;     // how far along the arc the dots reach
    public float aimSpacing = 0.5f;   // world units between dots

    [Header("Shadow walls")]
    [Tooltip("Layer of the shadow walls (the barriers, tag Barrier). Thrown light breaks them; with 0 light you slip through them.")]
    public LayerMask shadowMask;

    [Header("Look")]
    [Tooltip("Dark visor (the player's eyes); flips to the side you're facing.")]
    public Transform visor;

    [Header("Checkpoints")]
    [Tooltip("How many thrown light orbs a checkpoint has to absorb before it lights up.")]
    public int checkpointCost = 2;
    public float checkpointMinLight = 45f;
    public float respawnInvulnerability = 1.2f;

    public bool HasLight => lightAmount > 0f;
    public float LightRadius => _light != null ? _light.pointLightOuterRadius : 0f;
    /// <summary>Centre of the light circle (it leads ahead of the player). Enemies notice you from here.</summary>
    public Vector2 LightCenter => lightMask != null ? (Vector2)lightMask.position : (Vector2)transform.position;

    // Checkpoint data survives the scene reload that happens on death.
    static string _cpScene;
    static Vector2? _cpPos;
    static float _cpLight;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _cpScene = null; _cpPos = null; }

    Rigidbody2D _rb;
    Light2D _light;
    Collider2D _col;
    SpriteRenderer[] _sprites;
    bool _won;
    Collider2D[] _shadowCols = new Collider2D[0];
    SpriteRenderer[] _shadowSprites = new SpriteRenderer[0];
    bool _phased;
    int _facing = 1;
    float _invulnUntil;
    string _msg;
    float _msgUntil, _flashAt = -10f;
    GUIStyle _style;
    static Texture2D _dot;
    readonly System.Collections.Generic.Dictionary<GameObject, int> _charge = new System.Collections.Generic.Dictionary<GameObject, int>();

    /// <summary>The player in the current scene (thrown orbs use it to charge checkpoints).</summary>
    public static Player Instance { get; private set; }

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _light = lightMask.GetComponent<Light2D>();
        _col = GetComponent<Collider2D>();
        _sprites = GetComponentsInChildren<SpriteRenderer>();
        var walls = GameObject.FindGameObjectsWithTag("Barrier");
        _shadowCols = new Collider2D[walls.Length];
        _shadowSprites = new SpriteRenderer[walls.Length];
        for (int i = 0; i < walls.Length; i++) { _shadowCols[i] = walls[i].GetComponent<Collider2D>(); _shadowSprites[i] = walls[i].GetComponent<SpriteRenderer>(); }
        Instance = this;

        // respawn at the active checkpoint, if any
        if (_cpPos.HasValue && _cpScene == SceneManager.GetActiveScene().name)
        {
            transform.position = _cpPos.Value + Vector2.up * 0.5f;
            lightAmount = _cpLight;
            _invulnUntil = Time.time + respawnInvulnerability;
            foreach (var cp in GameObject.FindGameObjectsWithTag("Checkpoint"))
                if (Vector2.Distance(cp.transform.position, _cpPos.Value) < 0.01f) LightUp(cp, 1f);
            Show("Back at the checkpoint");
        }
        else { _cpPos = null; _cpScene = null; }
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb != null && kb.rKey.wasPressedThisFrame)
        {
            if (_won || kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) RestartLevel();
            else Respawn();
        }
        if (_won || kb == null) return;

        // run + jump
        float x = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
        Vector2 v = _rb.linearVelocity;
        v.x = x * moveSpeed;
        int standOn = _phased ? (int)groundMask : (groundMask | shadowMask); // can't stand on a shadow wall you're passing through
        bool grounded = Physics2D.OverlapBox((Vector2)_col.bounds.center + Vector2.down * _col.bounds.extents.y, new Vector2(_col.bounds.size.x * 0.9f, 0.1f), 0f, standOn);
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
        _light.pointLightOuterRadius = r;
        _light.pointLightInnerRadius = r * GlowInner; // soft glow that fades toward the edge

        // light leads ahead in the direction you're moving (and stays facing that way when you stop)
        if (x != 0f) _facing = x > 0f ? 1 : -1;
        UpdateShadowWalls();
        if (visor != null) visor.localPosition = new Vector3(Mathf.Abs(visor.localPosition.x) * _facing, visor.localPosition.y, 0f);
        Vector3 lead = new Vector3(_facing * r * lightLead, 0f, 0f);
        lightMask.localPosition = Vector3.Lerp(lightMask.localPosition, lead, Time.deltaTime * 5f);

        // blink while invulnerable after a respawn
        bool blink = Time.time < _invulnUntil && Mathf.Repeat(Time.time * 10f, 1f) < 0.5f;
        foreach (var s in _sprites) if (s != null) s.enabled = !blink;

        if (transform.position.y < -4f) Respawn();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("Shard")) { lightAmount = Mathf.Min(maxLight, lightAmount + 40f); Destroy(other.gameObject); Show("+40 light"); }
        else if (other.CompareTag("Checkpoint") && !IsActive(other.gameObject))
            Show($"Throw {checkpointCost - Charge(other.gameObject)} light orb(s) at the checkpoint to light it");
        else if (other.CompareTag("Finish")) { _won = true; _rb.linearVelocity = Vector2.zero; _cpPos = null; }
        else if (other.CompareTag("Enemy") && Time.time >= _invulnUntil) Respawn();
    }

    // so an enemy you're still touching when invulnerability ends still counts
    void OnCollisionEnter2D(Collision2D hit)
    {
        if (hit.gameObject.CompareTag("Barrier") && HasLight) Show("Shadow wall: break it with light, or slip through with no light");
    }

    // Shadow walls are solid while you have light but with 0 light you pass through them.
    // Uses per-collider IgnoreCollision
    void UpdateShadowWalls()
    {
        bool dark = !HasLight;
        if (dark == _phased) return;
        // got light back while inside a wall? stay ghostly until you're out, so you don't get stuck
        if (!dark && Physics2D.OverlapBox(_col.bounds.center, _col.bounds.size * 0.95f, 0f, shadowMask) != null) return;
        _phased = dark;
        foreach (var c in _shadowCols) if (c != null) Physics2D.IgnoreCollision(_col, c, dark);
        foreach (var s in _shadowSprites)
            if (s != null) { var col = s.color; col.a = dark ? 0.35f : 0.9f; s.color = col; }
        if (dark) Show("No light: you can slip through shadow walls");
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (!_won && other.CompareTag("Enemy") && Time.time >= _invulnUntil) Respawn();
    }

    bool IsActive(GameObject cp) => _cpPos.HasValue && Vector2.Distance(_cpPos.Value, cp.transform.position) < 0.01f;
    int Charge(GameObject cp) => _charge.TryGetValue(cp, out int n) ? n : 0;

    /// <summary>
    /// Called by a thrown orb that flies into a checkpoint. Returns true if the checkpoint absorbed it and the orb is used up), false if this checkpoint is already lit (the orb flies on through).
    /// </summary>
    public bool FeedCheckpoint(GameObject cp)
    {
        if (IsActive(cp)) return false;
        int n = Charge(cp) + 1;
        _charge[cp] = n;
        if (n < checkpointCost)
        {
            LightUp(cp, (float)n / checkpointCost);
            Show($"Checkpoint {n}/{checkpointCost}");
            return true;
        }
        ActivateCheckpoint(cp);
        return true;
    }

    void ActivateCheckpoint(GameObject cp)
    {
        // the previously active checkpoint (if it's in this scene) goes back to unlit, and needs charging again
        foreach (var other in GameObject.FindGameObjectsWithTag("Checkpoint"))
            if (other != cp && IsActive(other)) { LightUp(other, 0f); _charge.Remove(other); }
        lightAmount = Mathf.Max(lightAmount, checkpointMinLight);
        _cpScene = SceneManager.GetActiveScene().name;
        _cpPos = cp.transform.position;
        _cpLight = lightAmount;
        _charge.Remove(cp);
        LightUp(cp, 1f);
        _flashAt = Time.time;
        Show("Checkpoint lit");
    }

    // A checkpoint's look: a dim warm glow when unlit that grows with each orb (t = 0..1), and then a bigger green-white light once it's your respawn point (t = 1).
    static void LightUp(GameObject cp, float t)
    {
        bool on = t >= 1f;
        var l = cp.GetComponentInChildren<Light2D>();
        if (l != null)
        {
            l.pointLightOuterRadius = on ? 2.6f : 1.2f + 0.6f * t;
            l.pointLightInnerRadius = l.pointLightOuterRadius * GlowInner;
            l.intensity = on ? 1f : 0.5f + 0.3f * t;
            l.color = on ? new Color(0.6f, 1f, 0.75f) : new Color(1f, 0.8f, 0.5f);
        }
        var flame = cp.transform.Find("Flame");
        if (flame != null)
            flame.GetComponent<SpriteRenderer>().color = on ? new Color(0.5f, 1f, 0.7f) : Color.Lerp(new Color(1f, 0.6f, 0.25f), new Color(1f, 0.9f, 0.6f), t);
    }

    // Reload the level and start from the last active checkpoint by pressing R.
    void Respawn() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

    // Press Shift+R, or R after winning to start the level over from the beginning.
    void RestartLevel()
    {
        _cpPos = null;
        Respawn();
    }

    void Show(string msg, float seconds = 2f) { _msg = msg; _msgUntil = Time.time + seconds; }

    // Aim radar
    // Grey when you're out of light.
    void DrawAim(float k)
    {
        var mouse = Mouse.current;
        var cam = Camera.main;
        if (!showAim || _won || mouse == null || cam == null || orbTemplate == null) return;
        if (_dot == null) _dot = MakeCircle(32);

        Vector2 start = transform.position;
        Vector2 aim = ((Vector2)cam.ScreenToWorldPoint(mouse.position.ReadValue()) - start).normalized;
        Vector2 p = start + aim * 0.6f, v = aim * throwSpeed;
        Vector2 g = Physics2D.gravity * orbTemplate.gravityScale;
        var old = GUI.color;
        GUI.color = HasLight ? new Color(1f, 0.92f, 0.6f, 0.8f) : new Color(0.6f, 0.6f, 0.6f, 0.6f);
        float size = 6f * k;

        const float step = 0.02f;
        float travelled = 0f, nextDot = aimSpacing;
        for (int i = 0; i < 400 && travelled < aimLength; i++)
        {
            Vector2 np = p + v * step + 0.5f * g * step * step;
            if (Physics2D.Linecast(p, np, groundMask | shadowMask).collider != null) break; // stop at walls/ground
            travelled += Vector2.Distance(p, np);
            p = np;
            v += g * step;
            if (travelled >= nextDot) { Blit(cam, p, _dot, size); nextDot += aimSpacing; }
        }
        GUI.color = old;
    }

    static void Blit(Camera cam, Vector2 world, Texture2D tex, float size)
    {
        Vector3 s = cam.WorldToScreenPoint(world);
        GUI.DrawTexture(new Rect(s.x - size * 0.5f, Screen.height - s.y - size * 0.5f, size, size), tex);
    }

    // a white dot with soft edges, generated once so there are no extra image files
    static Texture2D MakeCircle(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float r = n * 0.5f;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                float a = Mathf.Clamp01(r - d);   // soft outer edge
                px[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    void OnGUI()
    {
        float k = Screen.height / 720f; // scale the HUD with the window (WebGL builds are often small)
        if (_style == null) _style = new GUIStyle(GUI.skin.label) { richText = true };
        _style.fontSize = Mathf.RoundToInt(18 * k);
        _style.alignment = TextAnchor.UpperLeft;
        var white = Texture2D.whiteTexture;
        var old = GUI.color;

        if (Event.current.type == EventType.Repaint) DrawAim(k);

        // checkpoint flash
        float f = 1f - (Time.time - _flashAt) / 0.5f;
        if (f > 0f) { GUI.color = new Color(0.5f, 1f, 0.7f, 0.25f * f); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white); }

        // light bar
        float t = lightAmount / maxLight;
        var bar = new Rect(20 * k, 20 * k, 220 * k, 16 * k);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(bar.x - 2 * k, bar.y - 2 * k, bar.width + 4 * k, bar.height + 4 * k), white);
        GUI.color = t > 0.3f ? new Color(1f, 0.92f, 0.6f) : Color.Lerp(new Color(1f, 0.35f, 0.25f), new Color(1f, 0.92f, 0.6f), t / 0.3f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * t, bar.height), white);
        GUI.color = old;
        GUI.Label(new Rect(bar.xMax + 10 * k, bar.y - 4 * k, 200 * k, 26 * k), $"Light {Mathf.CeilToInt(lightAmount)}", _style);

        // status line
        string status = null;
        if (_won) status = "<b>You escaped!</b>  Press R to play again.";
        else if (Time.time < _msgUntil) status = _msg;
        else if (!HasLight) status = "Your light is gone. The dark hides you and lets you slip through shadow walls. Find a shard or checkpoint.";
        if (status != null) GUI.Label(new Rect(20 * k, 44 * k, 900 * k, 28 * k), status, _style);

        // controls, bottom-left
        _style.fontSize = Mathf.RoundToInt(14 * k);
        GUI.Label(new Rect(20 * k, Screen.height - 30 * k, 1400 * k, 24 * k),
                  "A/D or arrows: move     Space or W: jump     Mouse: aim     Left click: throw light     R: back to checkpoint     Shift+R: restart level", _style);
    }
}
