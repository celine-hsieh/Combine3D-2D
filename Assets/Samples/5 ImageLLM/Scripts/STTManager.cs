using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using System.Threading.Tasks;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Text;
using Debug = UnityEngine.Debug;
//using static System.Net.Mime.MediaTypeNames;
using System.Diagnostics;

namespace QuestCameraKit.OpenAI
{
    // --- Await 擴充：讓 Unity 2022 也能 await UnityWebRequest ---
    static class UnityWebRequestAwaitExtensions
    {
        public static Task AsTask(this UnityWebRequestAsyncOperation op)
        {
            var tcs = new TaskCompletionSource<bool>();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }

    public class SttManager : MonoBehaviour
    {
        public event Action<string> OnTranscriptionComplete;

        [Header("STT Settings")]
        [SerializeField] private int recordingMaximum = 5;

        [Header("Canvas Settings")]
        [SerializeField] private bool useCanvas = true;

        [Header("UI Components (Optional)")]
        [SerializeField] private Button recordButton;
        [SerializeField] private UnityEngine.UI.Text transcriptionText;
        [SerializeField] private Dropdown microphoneDropdown;

        [Header("Events")] public UnityEvent onRequestStarted;
        public UnityEvent onRequestSent;

        private ImageOpenAIConnector _imageOpenAIConnector;
        private AudioClip _clip;
        private string _apiKey = "YOUR_OPENAI_API_KEY";
        private const string FileName = "output.wav";
        private bool _isRecording;
        private float _time;

        private void Start()
        {
            _imageOpenAIConnector = FindAnyObjectByType<ImageOpenAIConnector>();
            _apiKey = _imageOpenAIConnector != null ? _imageOpenAIConnector.apiKey : _apiKey;

            if (useCanvas)
            {
                if (microphoneDropdown != null)
                {
                    microphoneDropdown.options.Clear();
                    foreach (var device in Microphone.devices)
                        microphoneDropdown.options.Add(new Dropdown.OptionData(device));
                    microphoneDropdown.onValueChanged.AddListener(ChangeMicrophone);
                }

                if (recordButton != null)
                    recordButton.onClick.AddListener(ToggleRecording);
            }
        }

        private void ChangeMicrophone(int index)
        {
            PlayerPrefs.SetInt("user-mic-device-index", index);
        }

        private void ToggleRecording()
        {
            if (_isRecording) EndRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            onRequestStarted.Invoke();
            _isRecording = true;
            _time = 0;
            if (useCanvas && transcriptionText) transcriptionText.text = "Recording...";

            var index = PlayerPrefs.GetInt("user-mic-device-index", 0);
            string selectedMic = "";
            if (useCanvas && microphoneDropdown && microphoneDropdown.options.Count > index)
                selectedMic = microphoneDropdown.options[index].text;
            else
                selectedMic = Microphone.devices.Length > 0 ? Microphone.devices[0] : "";

            if (string.IsNullOrEmpty(selectedMic) || !Microphone.devices.Contains(selectedMic))
            {
                if (Microphone.devices.Length == 0)
                {
                    Debug.LogError("No microphone devices found!");
                    if (useCanvas && transcriptionText) transcriptionText.text = "No microphone found!";
                    _isRecording = false;
                    return;
                }
                Debug.LogWarning("Selected microphone not found, using default.");
                selectedMic = Microphone.devices[0];
            }

            _clip = Microphone.Start(selectedMic, false, recordingMaximum, 44100);

            if (_clip) return;

            Debug.LogError("Failed to start microphone recording!");
            if (useCanvas && transcriptionText) transcriptionText.text = "Mic recording failed!";
            _isRecording = false;
            if (useCanvas && recordButton) recordButton.interactable = true;
        }

        private async void EndRecording()
        {
            if (!_isRecording) return;

            onRequestSent.Invoke();
            _isRecording = false;
            if (useCanvas && transcriptionText) transcriptionText.text = "Processing...";
            Microphone.End(null);

            if (!_clip)
            {
                Debug.LogError("AudioClip is null! Cannot save.");
                if (useCanvas && transcriptionText) transcriptionText.text = "Recording failed!";
                if (useCanvas && recordButton) recordButton.interactable = true;
                return;
            }

            byte[] audioData = SaveWav.Save(FileName, _clip);
            string transcription = await SendToOpenAI(audioData);

            if (useCanvas && transcriptionText) transcriptionText.text = transcription;
            if (useCanvas && recordButton) recordButton.interactable = true;

            OnTranscriptionComplete?.Invoke(transcription);
        }

        private async Task<string> SendToOpenAI(byte[] audioData)
        {
            var url = "https://api.openai.com/v1/audio/transcriptions";

            if (audioData == null || audioData.Length == 0)
            {
                Debug.LogError("SendToOpenAI: Audio data is empty or null.");
                return "Error: Audio file is empty.";
            }

            if (audioData.Length > 25 * 1024 * 1024)
            {
                Debug.LogError("SendToOpenAI: Audio file is too large.");
                return "Error: File too large.";
            }

            var filePath = Path.Combine(Application.persistentDataPath, FileName);
            await File.WriteAllBytesAsync(filePath, audioData);

            // 建立 multipart/form-data（Unity 2022 方式）
            var formData = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", audioData, FileName, "audio/wav"),
                new MultipartFormDataSection("model", "whisper-1")
            };

            byte[] boundary = UnityWebRequest.GenerateBoundary();
            byte[] formDataBytes = UnityWebRequest.SerializeFormSections(formData, boundary);
            var contentType = "multipart/form-data; boundary=" + Encoding.ASCII.GetString(boundary);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(formDataBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", contentType);
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);

                // 這裡用我們自建的 awaiter（Unity 2022 沒有 GetAwaiter）
                var op = request.SendWebRequest();
                await op.AsTask();

                if (request.result != UnityWebRequest.Result.ConnectionError &&
                    request.result != UnityWebRequest.Result.ProtocolError)
                {
                    return request.downloadHandler.text;
                }

                Debug.LogError("OpenAI API Error: " + request.error + "\nResponse: " + request.downloadHandler.text);
                return "Error: " + request.downloadHandler.text;
            }
        }

        private void Update()
        {
            if (_isRecording)
            {
                _time += Time.deltaTime;
                if (_time >= recordingMaximum) EndRecording();
            }

            if (OVRInput.GetDown(OVRInput.Button.Start)) ToggleRecording();
        }
    }
}
