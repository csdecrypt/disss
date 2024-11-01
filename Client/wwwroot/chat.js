document.addEventListener("DOMContentLoaded", () => {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/datahub")
        .configureLogging(signalR.LogLevel.Information)
        .build();

    connection.on("EventGridMessage", (message) => {
        console.log(message);

        if (message.type == "Transcribed") {
            const messageList = document.getElementById('messageList');
            message.data.chunks.forEach((chunk) => {
                messageList.innerHTML += chunk.text + '<br>'; 

            })
            messageList.scrollTop = eventList.scrollHeight;
        }
        else if (message.type == "Matched") {
            const eventList = document.getElementById('eventList');
            message.data.forEach((match) => {
                eventList.innerHTML += `Matched ${match.Keyword} with score: ${match.Score}` + '<br>';
            })
            eventList.scrollTop = eventList.scrollHeight;
        }
        else if (message.type == "SystemEvent") {
            const eventList = document.getElementById('eventList');
            eventList.innerHTML += message.data + '<br>'; 
            eventList.scrollTop = eventList.scrollHeight;
        }
        else if (message.type == "PhotoFound") {
            displayImage(message.data);
        }
    });
    function displayImage(url) {
        const img = document.getElementById('center-image');

        // Remove the 'fade-out' class and set the new image source
        img.classList.remove('fade-out');
        img.src = url;

        // Apply fade-out effect after a delay (so image is visible briefly before fading out)
        setTimeout(() => {
            img.classList.add('fade-out');
        }, 3000); // Show the image for 1 second before fading out

        // Once fade-out is complete, reset image src and fade-out class
        img.addEventListener('transitionend', function handleTransitionEnd() {
            img.removeEventListener('transitionend', handleTransitionEnd); // Clean up event listener
        });
    }

    let mediaRecorder;
    let isRecording = false;

    // Function to start recording and send 10-second chunks
    async function startRecording() {
        navigator.mediaDevices.getUserMedia({ audio: true })
            .then(stream => {
                mediaRecorder = new MediaRecorder(stream);

                // Buffer to store chunks of audio data
                let audioChunks = [];

                mediaRecorder.ondataavailable = (event) => {
                    audioChunks.push(event.data);
                };

                mediaRecorder.onstop = async () => {
                    console.log("onstop")
                    // Create Blob from audio chunks
                    const webmBlob = new Blob(audioChunks, { type: 'audio/webm' });

                    // Convert WebM to WAV directly using the convertWebMToWav method
                    const wavBlob = await convertWebMToWav(webmBlob);

                    // Read the WAV Blob and convert it to Base64
                    const reader = new FileReader();
                    reader.onloadend = async () => {
                        const base64AudioMessage = reader.result.split(',')[1]; // Get base64 portion
                        try {
                            // Send Base64 audio data to the server (without user parameter)
                            await connection.invoke("SendData", base64AudioMessage);
                        } catch (err) {
                            console.error("Error sending audio data:", err);
                        }
                    };
                    reader.readAsDataURL(wavBlob); // Convert WAV blob to base64
                    audioChunks = [];

                    if (isRecording) {
                        console.log("started after stop")
                        mediaRecorder.start();
                        setTimeout(() => mediaRecorder.stop(), 10000);
                    }
                };

                // Start recording for 10 seconds
                if (isRecording) {
                    console.log("started initially")
                    mediaRecorder.start();
                    setTimeout(() => mediaRecorder.stop(), 10000);
                }
               
            })
            .catch(error => {
                console.error("Error accessing microphone:", error);
            });
    }

    document.getElementById("send").addEventListener("click", async () => {
        if (!isRecording) {
            // Start recording if not already recording
            isRecording = true;
            startRecording();
        } else {
            console.log("Already recording...");
        }
    });

    document.getElementById("stop").addEventListener("click", () => {
        if (isRecording) {
            mediaRecorder.stop(); // Stop the recording
            isRecording = false;
        }
    });

    async function start() {
        try {
            await connection.start();
            console.log("SignalR Connected.");
        } catch (err) {
            console.log(err);
            setTimeout(start, 5000);
        }
    }

    connection.onclose(async () => {
        await start();
    });

    // Start the connection.
    start();

    // Function to convert WebM to WAV at 16kHz
    async function convertWebMToWav(webmBlob) {
        // Convert Blob to ArrayBuffer using FileReader for broader browser compatibility
        const arrayBuffer = await blobToArrayBuffer(webmBlob);

        // Create AudioContext and decode the audio data
        const audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
        const audioBuffer = await audioContext.decodeAudioData(arrayBuffer);

        // Resample the audio to 16kHz
        const offlineContext = new OfflineAudioContext({
            numberOfChannels: audioBuffer.numberOfChannels,
            length: audioBuffer.length * 16000 / audioBuffer.sampleRate,
            sampleRate: 16000
        });

        const source = offlineContext.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(offlineContext.destination);
        source.start();

        // Render the audio buffer at the new sample rate (16kHz)
        const resampledBuffer = await offlineContext.startRendering();

        // Convert the resampled audio buffer to WAV
        const wavBlob = audioBufferToWav(resampledBuffer);
        return wavBlob;
    }

    // Helper function to convert Blob to ArrayBuffer using FileReader for broader browser compatibility
    function blobToArrayBuffer(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onloadend = () => resolve(reader.result);
            reader.onerror = reject;
            reader.readAsArrayBuffer(blob);
        });
    }

    // Function to convert audio buffer to WAV Blob
    function audioBufferToWav(buffer) {
        const numberOfChannels = buffer.numberOfChannels;
        const sampleRate = buffer.sampleRate;
        const format = 1; // PCM

        const interleaved = interleaveChannels(buffer);
        const wavBuffer = createWavFile(interleaved, numberOfChannels, sampleRate, format);

        // Create WAV Blob
        return new Blob([wavBuffer], { type: 'audio/wav' });
    }

    // Function to interleave channels into a single array
    function interleaveChannels(buffer) {
        const numberOfChannels = buffer.numberOfChannels;
        const length = buffer.length * numberOfChannels;
        const result = new Float32Array(length);

        let index = 0;
        for (let i = 0; i < buffer.length; i++) {
            for (let channel = 0; channel < numberOfChannels; channel++) {
                result[index++] = buffer.getChannelData(channel)[i];
            }
        }

        return result;
    }

    // Function to create WAV file buffer from interleaved audio data
    function createWavFile(samples, numberOfChannels, sampleRate, format) {
        const blockAlign = numberOfChannels * 2; // 2 bytes per sample (16-bit)
        const byteRate = sampleRate * blockAlign;
        const buffer = new ArrayBuffer(44 + samples.length * 2);
        const view = new DataView(buffer);

        // RIFF chunk descriptor
        writeString(view, 0, 'RIFF');
        view.setUint32(4, 36 + samples.length * 2, true); // file size
        writeString(view, 8, 'WAVE');

        // fmt sub-chunk
        writeString(view, 12, 'fmt ');
        view.setUint32(16, 16, true); // sub-chunk size
        view.setUint16(20, format, true); // audio format (1 for PCM)
        view.setUint16(22, numberOfChannels, true); // number of channels
        view.setUint32(24, sampleRate, true); // sample rate
        view.setUint32(28, byteRate, true); // byte rate
        view.setUint16(32, blockAlign, true); // block align
        view.setUint16(34, 16, true); // bits per sample

        // data sub-chunk
        writeString(view, 36, 'data');
        view.setUint32(40, samples.length * 2, true); // data size

        // Write interleaved audio samples as 16-bit PCM
        let offset = 44;
        for (let i = 0; i < samples.length; i++, offset += 2) {
            const sample = Math.max(-1, Math.min(1, samples[i])); // Clamp between -1 and 1
            view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7FFF, true); // Convert to 16-bit PCM
        }

        return view;
    }

    // Helper function to write strings to DataView
    function writeString(view, offset, string) {
        for (let i = 0; i < string.length; i++) {
            view.setUint8(offset + i, string.charCodeAt(i));
        }
    }
});