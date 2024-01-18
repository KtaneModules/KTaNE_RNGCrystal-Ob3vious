using KTMissionGetter;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Rnd = UnityEngine.Random;

public class RngCrystalScript : MonoBehaviour
{

    private KMBombModule _module;
    private KMAudio _audio;

    [SerializeField]
    private KMSelectable _selectable;
    [SerializeField]
    private KMSelectable _altSelectable;

    [SerializeField]
    private Transform _progressBar;
    [SerializeField]
    private Transform _stateIndicator;

    [SerializeField]
    private MeshRenderer _crystal;
    [SerializeField]
    private MeshRenderer _loadingCase;
    [SerializeField]
    private MeshRenderer _statusCase;

    [SerializeField]
    private TextMesh[] _text;

    private List<TextMesh> _zawaText;

    private static int _moduleIdCounter = 1;
    private int _moduleId;

    private KMAudio.KMAudioRef _audioRef = null;

    private float _barProgress = 0;

    private int _luck = -1;
    private int _tossCount = 0;

    private TextColor _textColor;
    private float _colorCycle;

    private const float LoadingTime = 1.5f;
    private const float RetractTime = 1f;
    private const float ColorCycleTime = 2f;

    private const int ConsecutiveFlipsToWin = 11;
    private static readonly string[] LuckLabels = { "不運", "平運", "好運", "強運", "淒運", "幸運", "魅運", "激運", "超運", "豪運", "剛運", "神運" };

    private enum ModuleStatus
    {
        Idle,
        Charged,
        Tossing,
        AltIdle,
        AltCharged,
        AltTossing,
        Solved
    }

    private enum ModuleSolveStyle
    {
        None,
        Gambler,
        Nerd,
        Solver
    }

    private ModuleStatus _status;
    private ModuleSolveStyle _style = ModuleSolveStyle.None;
    private bool _holding = false;
    private bool _altHolding = false;

    private bool _isModuleSubmitting = false;

    private int _register;
    private int _taps;

    void Awake()
    {
        _module = GetComponent<KMBombModule>();
        _audio = GetComponent<KMAudio>();
    }

    void Start()
    {
        HandleMissionDescription();

        _moduleId = _moduleIdCounter++;

        _selectable.OnInteract = OnInteract;
        _selectable.OnInteractEnded = OnInteractEnded;

        _altSelectable.OnInteract = AltOnInteract;
        _altSelectable.OnInteractEnded = AltOnInteractEnded;

        KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
        moduleSelectable.OnFocus = PlayAudioRef;
        moduleSelectable.OnDefocus = StopAudioRef;

        _zawaText = new List<TextMesh> { _text[3] };
        Zawa(0, 0, 0, false);

        _crystal.enabled = false;

        UpdateBarProgress();
        UpdateIndicatorState();

        _status = ModuleStatus.Idle;

        _taps = GenerateLfsrPolynomial() >> 1;
        _register = Rnd.Range(1, 1 << LfsrPolynomialDegree((_taps << 1) | 1));
        Log("Random number generator has been set up with taps of {0} and register {1}.", Convert.ToString(_taps, 2), Convert.ToString(_register, 2).PadLeft(LfsrPolynomialDegree((_taps << 1) | 1), '0'));
    }

    void Update()
    {
        _colorCycle = (_colorCycle + Time.deltaTime / ColorCycleTime) % 1;

        _crystal.transform.localEulerAngles = new Vector3(90, 0, 360 * 2 * _colorCycle);

        if (_status != ModuleStatus.Tossing || _isModuleSubmitting)
            UpdateLuckText();

        if (_status == ModuleStatus.Tossing)
        {
            int sign = _barProgress < 0 ? -1 : 1;
            _barProgress = sign * Mathf.Max((sign * _barProgress + 1) * Mathf.Pow(0.5f, Time.deltaTime / RetractTime) - 1, 0);

            UpdateBarProgress();

            if (_barProgress != 0)
                return;

            if (_isModuleSubmitting)
            {
                _isModuleSubmitting = false;
                UpdateIndicatorState();
                _status = ModuleStatus.Idle;
            }
            return;
        }
        else if (_status == ModuleStatus.AltTossing)
        {
            int sign = _barProgress < 0 ? -1 : 1;
            _barProgress = sign * Mathf.Max((sign * _barProgress + 1) * Mathf.Pow(0.5f, Time.deltaTime / RetractTime) - 1, 0);

            UpdateBarProgress();

            if (_barProgress != 0)
                return;

            _status = ModuleStatus.AltIdle;
        }

        if (_status == ModuleStatus.Solved)
        {
            return;
        }

        if (_holding && (_status == ModuleStatus.Idle || _status == ModuleStatus.AltIdle))
        {
            _barProgress += Time.deltaTime / LoadingTime;

            if (_barProgress > 1)
            {
                _barProgress = 1;
                _status = _status == ModuleStatus.Idle ? ModuleStatus.Charged : ModuleStatus.AltCharged;
            }
        }

        if (_altHolding && (_status == ModuleStatus.Idle || _status == ModuleStatus.AltIdle))
        {
            _barProgress -= Time.deltaTime / LoadingTime;

            if (_barProgress < -1)
            {
                _barProgress = -1;
                _status = ModuleStatus.AltCharged;
            }
        }

        UpdateBarProgress();
    }

    void OnDestroy()
    {
        StopAudioRef();
    }

    private bool OnInteract()
    {
        _selectable.AddInteractionPunch(1);
        _holding = true;
        return false;
    }

    private void OnInteractEnded()
    {
        _holding = false;
        if (_status == ModuleStatus.Charged)
        {
            if (_crystal.enabled)
            {
                Solve();
                return;
            }

            _status = ModuleStatus.Tossing;
            StartCoroutine(Toss());
        }
        else if (_status == ModuleStatus.AltCharged)
        {
            if (_luck >= ConsecutiveFlipsToWin)
            {
                Solve();
                return;
            }

            _status = ModuleStatus.AltTossing;

            CheckToss(true);
        }
    }

    private bool AltOnInteract()
    {
        _altSelectable.AddInteractionPunch(1);
        if (_luck < ConsecutiveFlipsToWin && (_style == ModuleSolveStyle.None || _style == ModuleSolveStyle.Nerd))
            _altHolding = true;
        return false;
    }

    private void AltOnInteractEnded()
    {
        _altHolding = false;
        if (_status == ModuleStatus.AltCharged)
        {
            _status = ModuleStatus.AltTossing;

            if (_isModuleSubmitting)
                CheckToss(false);
            else
            {
                _luck = 0;
                _isModuleSubmitting = true;
                UpdateIndicatorState();

                Log("Alternate input method has been activated. Good luck!");

                _audio.PlaySoundAtTransform("Zawa1", transform);
            }
        }
    }

    private void CheckToss(bool toss)
    {
        if (TossCoin() == toss)
        {
            Log("You predicted {0}. This was correct.", toss ? "Heads" : "Tails");

            _luck++;

            UpdateLuckText();
            if (_luck >= ConsecutiveFlipsToWin)
            {
                _isModuleSubmitting = false;
                UpdateIndicatorState();

                _style = ModuleSolveStyle.Nerd;
                SetCrystal();
                return;
            }

            int[] barriers = { 2, 4, 7, 10 };
            _audio.PlaySoundAtTransform("Zawa" + (char)(barriers.Count(x => _luck >= x) + '1'), transform);

            return;
        }

        Log("You predicted {0}. This was not correct.", toss ? "Heads" : "Tails");

        _module.HandleStrike();
        _status = ModuleStatus.Tossing;
        _luck = 0;
    }

    private void PlayAudioRef()
    {
        if (_audioRef != null)
        {
            StopAudioRef();
        }

        if (_luck >= ConsecutiveFlipsToWin && _status != ModuleStatus.Tossing)
        {
            _audioRef = _audio.PlaySoundAtTransformWithRef("SolvedLoop", transform);
        }
        else
        {
            _audioRef = _audio.PlaySoundAtTransformWithRef("UnsolvedLoop", transform);
        }
    }

    private void StopAudioRef()
    {
        if (_audioRef != null)
        {
            _audioRef.StopSound();
            _audioRef = null;
        }
    }

    public IEnumerator Toss()
    {
        _text[0].text = "";
        _text[1].text = "";
        _text[2].text = "";

        _tossCount++;
        _luck = 0;
        while (_luck < ConsecutiveFlipsToWin || !(_style == ModuleSolveStyle.None || _style == ModuleSolveStyle.Gambler))
        {
            if (TossCoin())
            {
                _luck++;
            }
            else
            {
                break;
            }
        }

        int[] barriers = { 0, 2, 4, 7, 10 };
        int[][] sizeCounts = { new int[] { 2, 0, 0 }, new int[] { 5, 0, 0 }, new int[] { 5, 5, 0 }, new int[] { 5, 0, 10 }, new int[] { 5, 5, 10 } };
        bool[] red = { false, false, false, false, true };

        for (int i = 0; i < 5 && _luck >= barriers[i]; i++)
        {
            _audio.PlaySoundAtTransform("Zawa" + (char)(i + '1'), transform);
            Zawa(sizeCounts[i][0], sizeCounts[i][1], sizeCounts[i][2], red[i]);
            yield return new WaitForSeconds(1);
        }

        _status = ModuleStatus.Idle;
        _barProgress = 0;
        Zawa(0, 0, 0, false);
        UpdateBarProgress();
        UpdateLuckText();
        LogLuck();

        if (_luck >= ConsecutiveFlipsToWin && (_style == ModuleSolveStyle.None || _style == ModuleSolveStyle.Gambler))
        {
            _style = ModuleSolveStyle.Gambler;
            SetCrystal();
        }
    }

    private bool TossCoin()
    {
        _register = LfsrShiftRegister(_register, _taps);
        return _register % 2 == 1;
    }

    private static int LfsrShiftRegister(int register, int taps)
    {
        int tapped = register & taps;
        int xor = 0;
        while (tapped > 0)
        {
            xor ^= tapped & 1;
            tapped >>= 1;
        }
        return ((register << 1) | xor) & ((1 << LfsrPolynomialDegree((taps << 1) | 1)) - 1);
    }

    private static List<int> _candidatePolynomials = null;
    private static int GenerateLfsrPolynomial()
    {
        if (_candidatePolynomials != null)
            return _candidatePolynomials.PickRandom();

        int lowerDegreeBound = 18;
        int upperDegreeBound = 24;

        _candidatePolynomials = new List<int>();

        for (int degree = lowerDegreeBound; degree <= upperDegreeBound; degree++)
        {
            int parameterCount = 0;
            List<int> candidates = new List<int>();
            while (parameterCount < 4)
            {
                Stack<int> trial = new Stack<int>();
                trial.Push(degree);
                parameterCount += 2;

                while (degree > parameterCount)
                {
                    while (trial.Count < parameterCount)
                        trial.Push(trial.Peek() - 1);

                    if (trial.Peek() < 1)
                    {
                        while (trial.Peek() < 1)
                        {
                            trial.Pop();
                            trial.Push(trial.Pop() - 1);
                        }

                        if (trial.Count <= 1)
                            break;

                        continue;
                    }

                    int polynomial = trial.Sum(x => 1 << x) | 1;
                    if (LfsrPolynomialPrimitive(polynomial))
                        candidates.Add(polynomial);

                    trial.Push(trial.Pop() - 1);
                }
            }

            _candidatePolynomials.AddRange(candidates);
        }

        return _candidatePolynomials.PickRandom();
    }

    private static bool LfsrPolynomialPrimitive(int polynomial)
    {
        int checkCap = 1 << (LfsrPolynomialDegree(polynomial) / 2 + 1);
        for (int trial = 3; trial < checkCap; trial += 2)
            if (LfsrPolynomialDivides(polynomial, trial))
                return false;
        return true;
    }

    private static bool LfsrPolynomialDivides(int polynomial, int divisor)
    {
        int divisorDegree = LfsrPolynomialDegree(divisor);
        while (polynomial >= divisor)
            polynomial ^= divisor << (LfsrPolynomialDegree(polynomial) - divisorDegree);
        return polynomial == 0;
    }

    private static int LfsrPolynomialDegree(int polynomial)
    {
        int i = -1;
        while (polynomial > 0)
        {
            polynomial >>= 1;
            i++;
        }
        return i;
    }

    private struct RectangleCover
    {
        public float MinX { get; private set; }
        public float MinY { get; private set; }
        public float MaxX { get; private set; }
        public float MaxY { get; private set; }

        public RectangleCover(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public bool Overlaps(RectangleCover other)
        {
            if (MinX >= other.MaxX || MaxX <= other.MinX)
            {
                return false;
            }
            if (MinY >= other.MaxY || MaxY <= other.MinY)
            {
                return false;
            }
            return true;
        }
    }

    private void Zawa(int large, int medium, int small, bool red)
    {
        float minX = -0.08f;
        float maxX = 0.08f;
        float minZ = -0.05f;
        float maxZ = 0.08f;
        float[] scaleFactors = { 0.375f, 0.75f, 1.5f };
        float xOff = 1 / 45f;
        float zOff = 1 / 150f;

        int attemptCount = 10000;

        while (_zawaText.Count < large + medium + small)
        {
            _zawaText.Add(Instantiate(_text[3], transform));
        }

        Queue<int> zawas = new Queue<int>(Enumerable.Repeat(0, small).Concat(Enumerable.Repeat(1, medium)).Concat(Enumerable.Repeat(2, large)).ToList().Shuffle());

        List<RectangleCover> currentlyPlaced = new List<RectangleCover>();

        while (zawas.Count > 0 && attemptCount-- > 0)
        {
            int type = zawas.Dequeue();
            float x = Rnd.Range(minX + xOff * scaleFactors[type], maxX - xOff * scaleFactors[type]);
            float z = Rnd.Range(minZ + zOff * scaleFactors[type], maxZ - zOff * scaleFactors[type]);
            RectangleCover cover = new RectangleCover(
                x - xOff * scaleFactors[type], z - zOff * scaleFactors[type],
                x + xOff * scaleFactors[type], z + zOff * scaleFactors[type]);

            if (currentlyPlaced.Any(c => c.Overlaps(cover)))
            {
                zawas.Enqueue(type);
            }
            else
            {
                UpdateLuckColor(_zawaText[currentlyPlaced.Count], red ? TextColor.Red : TextColor.White);
                _zawaText[currentlyPlaced.Count].transform.localPosition = new Vector3(x, 0.0151f, z);
                _zawaText[currentlyPlaced.Count].transform.localScale = Vector3.one * scaleFactors[type];
                _zawaText[currentlyPlaced.Count].GetComponent<MeshRenderer>().enabled = true;
                currentlyPlaced.Add(cover);
            }
        }

        for (int i = currentlyPlaced.Count; i < _zawaText.Count; i++)
        {
            _zawaText[i].GetComponent<MeshRenderer>().enabled = false;
        }
    }

    private void SetCrystal()
    {
        _crystal.enabled = true;
        switch (_style)
        {
            case ModuleSolveStyle.None:
                throw new Exception("The module must have been solved *somehow*");
            case ModuleSolveStyle.Gambler:
                _crystal.material.color = new Color32(0x80, 0xff, 0xff, 0xff);
                break;
            case ModuleSolveStyle.Nerd:
                _crystal.material.color = new Color32(0xff, 0xe0, 0x80, 0xff);
                break;
            case ModuleSolveStyle.Solver:
                _crystal.material.color = new Color32(0xc0, 0x80, 0xff, 0xff);
                break;
            default:
                break;
        }

        if (_audioRef != null)
        {
            PlayAudioRef();
        }
        _audio.PlaySoundAtTransform("CrystalShow", transform);
    }

    private void Solve()
    {
        _crystal.enabled = false;
        _module.HandlePass();
        _status = ModuleStatus.Solved;
        _isModuleSubmitting = false;
        UpdateIndicatorState();
        _loadingCase.material = _crystal.material;
        _statusCase.material = _crystal.material;
        _progressBar.GetComponent<MeshRenderer>().enabled = false;
        Log("Congratulations! You wasted time successfully!");
        _audio.PlaySoundAtTransform("CrystalGet", transform);
    }

    private void UpdateBarProgress()
    {
        int sign = _barProgress < 0 ? -1 : 1;

        _progressBar.localScale = new Vector3(1, _barProgress, 1);
        _progressBar.localPosition = new Vector3(0, sign * 0.011875f * (1 - sign * _barProgress), 0);
    }

    private void UpdateIndicatorState()
    {
        _stateIndicator.localScale = _isModuleSubmitting ? Vector3.one : Vector3.zero;
    }

    private enum TextColor
    {
        White,
        Red,
        Gold,
        Rainbow,
        Grey
    }

    private string GetProbability(int step)
    {
        if (step == 0)
            return "1";

        step--;
        List<int> output = new List<int> { 5 };

        while (step > 0)
        {
            for (int i = output.Count - 1; i >= 0; i--)
            {
                int passOn = (output[i] % 2) * 5;
                if (i == output.Count - 1 && passOn != 0)
                    output.Add(passOn);
                else if (passOn != 0)
                    output[i + 1] += passOn;

                output[i] /= 2;
            }
            step--;
        }

        return "0." + output.Join("");
    }

    private void UpdateLuckText()
    {
        if (_luck == -1)
        {
            _text[0].transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            _text[0].text = "圧倒的\n運っ…!";
            _text[1].text = "1";
            _text[2].text = "運否\n天賦";
            _textColor = TextColor.White;
        }
        else
        {
            _text[0].transform.localScale = new Vector3(1, 1, 1);
            if (_luck < LuckLabels.Length)
            {
                _text[0].text = LuckLabels[_luck];
            }
            else
            {
                _text[0].text = "何運";
            }
            _text[1].text = GetProbability(_luck + 1);
            _text[2].text = "もう\n一度";

            switch (_luck)
            {
                case 9:
                    _textColor = TextColor.Red;
                    break;
                case 10:
                    _textColor = TextColor.Gold;
                    break;
                case 11:
                    _textColor = TextColor.Rainbow;
                    _text[2].text = "";
                    break;
                default:
                    if (_luck < 9)
                        _textColor = TextColor.White;
                    else
                        _textColor = TextColor.Grey;
                    break;
            }

        }

        UpdateLuckColor(_text[0], _textColor);
        UpdateLuckColor(_text[1], _textColor);
    }

    private void UpdateLuckColor(TextMesh text, TextColor color)
    {
        //helper variable, compiler was complaining
        float colorValue;
        switch (color)
        {
            case TextColor.White:
                text.color = new Color(1, 1, 1);
                break;
            case TextColor.Red:
                text.color = new Color(1, 0, 0);
                break;
            case TextColor.Gold:
                colorValue = (Mathf.Sin(_colorCycle * Mathf.PI * 2) + 1) / 2;
                text.color = new Color(1, (5 + colorValue * 2) / 8, 0);
                break;
            case TextColor.Rainbow:
                int colorSection = (int)(_colorCycle * 6);
                colorValue = (_colorCycle * 6) % 1;
                switch (colorSection)
                {
                    case 0:
                        text.color = new Color(1, colorValue, 0);
                        break;
                    case 1:
                        text.color = new Color(1 - colorValue, 1, 0);
                        break;
                    case 2:
                        text.color = new Color(0, 1, colorValue);
                        break;
                    case 3:
                        text.color = new Color(0, 1 - colorValue, 1);
                        break;
                    case 4:
                        text.color = new Color(colorValue, 0, 1);
                        break;
                    case 5:
                        text.color = new Color(1, 0, 1 - colorValue);
                        break;
                }
                break;
            case TextColor.Grey:
                text.color = new Color(0.5f, 0.5f, 0.5f);
                break;
            default:
                break;
        }
    }

    private void LogLuck()
    {
        if (_style == ModuleSolveStyle.None || _style == ModuleSolveStyle.Gambler)
        {
            switch (_luck)
            {
                case ConsecutiveFlipsToWin:
                    Log("You finally did it! It only took you {0} tries.", _tossCount);
                    break;
                case ConsecutiveFlipsToWin - 1:
                    Log("You failed on the last toss. You can always try again.");
                    break;
                case 0:
                    Log("The first toss has failed you already. Try again.");
                    break;
                default:
                    Log("You have tossed successfully {0} times. Try again.", _luck);
                    break;
            }
        }
        else
        {
            Log("You have tossed successfully {0} times.", _luck);
        }

        Log("Current register: {0}.", Convert.ToString(_register, 2).PadLeft(LfsrPolynomialDegree((_taps << 1) | 1), '0'));
    }

    private void Log(string message, params object[] args)
    {
        Debug.LogFormat("[{0} #{1}] {2}", "RNG Crystal", _moduleId, string.Format(message, args));
    }

#pragma warning disable 414
    private string TwitchHelpMessage = "'!{0} toss' to give it a go. '!{0} switch' to go to the submission state. '!{0} heads/tails' to enter heads or tails. '!{0} collect' to get the crystal when it shows up. Good luck!";
#pragma warning restore 414
    IEnumerator ProcessTwitchCommand(string command)
    {
        yield return null;
        command = command.ToLowerInvariant();

        if (command == "collect" && _crystal.enabled)
        {
            KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
            moduleSelectable.OnFocus();
            _selectable.OnInteract();
            while (_status != ModuleStatus.Charged && _status != ModuleStatus.AltCharged)
            {
                yield return null;
            }
            _selectable.OnInteractEnded();
            moduleSelectable.OnDefocus();
            yield break;
        }
        else if (_crystal.enabled)
        {
            yield return "sendtochaterror Invalid command.";
            yield break;
        }

        if (command == "toss" && !_isModuleSubmitting)
        {
            KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
            moduleSelectable.OnFocus();
            _selectable.OnInteract();
            while (_status != ModuleStatus.Charged)
            {
                yield return null;
            }
            _selectable.OnInteractEnded();
            moduleSelectable.OnDefocus();
        }
        else if ((command == "switch" && !_isModuleSubmitting) || (command == "tails" && _isModuleSubmitting))
        {
            KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
            moduleSelectable.OnFocus();
            _altSelectable.OnInteract();
            while (_status != ModuleStatus.AltCharged)
            {
                yield return null;
            }
            _altSelectable.OnInteractEnded();
            moduleSelectable.OnDefocus();
        }
        else if (command == "heads" && _isModuleSubmitting)
        {
            KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
            moduleSelectable.OnFocus();
            _selectable.OnInteract();
            while (_status != ModuleStatus.AltCharged)
            {
                yield return null;
            }
            _selectable.OnInteractEnded();
            moduleSelectable.OnDefocus();
        }
        else
        {
            yield return "sendtochaterror Invalid command.";
            yield break;
        }
    }

    IEnumerator TwitchHandleForcedSolve()
    {
        if (_status == ModuleStatus.Solved)
            yield break;

        _luck = ConsecutiveFlipsToWin;
        _style = ModuleSolveStyle.Solver;
        SetCrystal();

        KMSelectable moduleSelectable = _module.GetComponent<KMSelectable>();
        moduleSelectable.OnFocus();
        _selectable.OnInteract();
        while (_status != ModuleStatus.Charged)
        {
            yield return null;
        }
        _selectable.OnInteractEnded();
        moduleSelectable.OnDefocus();

    }

    private void HandleMissionDescription()
    {
        string missionDesc = Mission.Description;
        if (missionDesc == null)
        {
            _style = ModuleSolveStyle.None;
            return;
        }

        Regex regex = new Regex(@"\[RNG Crystal\]:(LuckOnly|CalculationOnly)");
        var match = regex.Match(missionDesc);
        if (!match.Success)
        {
            _style = ModuleSolveStyle.None;
            return;
        }

        if (match.Value.Contains("LuckOnly"))
        {
            _style = ModuleSolveStyle.Gambler;
            return;
        }
        if (match.Value.Contains("CalculationOnly"))
        {
            _style = ModuleSolveStyle.Nerd;
            return;
        }

        _style = ModuleSolveStyle.None;
    }
}
