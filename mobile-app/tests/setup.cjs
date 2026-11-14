process.env.EXPO_PUBLIC_API_URL = "http://birdlab.test:5100";
process.env.EXPO_PUBLIC_POLL_INTERVAL_MS = "500";
global.FormData = jest.requireActual("react-native/Libraries/Network/FormData").default;

jest.mock("react-native-safe-area-context", () =>
  jest.requireActual("react-native-safe-area-context/jest/mock").default,
);
jest.mock("@expo/vector-icons", () => ({ Ionicons: () => null }));
jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));
jest.mock("expo-secure-store", () => ({
  getItemAsync: jest.fn(), setItemAsync: jest.fn(), deleteItemAsync: jest.fn(),
  WHEN_UNLOCKED_THIS_DEVICE_ONLY: "this-device",
}));
jest.mock("expo-image-picker", () => ({
  requestCameraPermissionsAsync: jest.fn(),
  requestMediaLibraryPermissionsAsync: jest.fn(),
  launchCameraAsync: jest.fn(),
  launchImageLibraryAsync: jest.fn(),
  UIImagePickerPreferredAssetRepresentationMode: { Compatible: "compatible" },
}));
jest.mock("expo-document-picker", () => ({ getDocumentAsync: jest.fn() }));
jest.mock("expo-sharing", () => ({ isAvailableAsync: jest.fn(), shareAsync: jest.fn() }));
jest.mock("expo-audio", () => ({
  RecordingPresets: { HIGH_QUALITY: "high" },
  requestRecordingPermissionsAsync: jest.fn(), setAudioModeAsync: jest.fn(),
  useAudioPlayer: jest.fn(), useAudioPlayerStatus: jest.fn(),
  useAudioRecorder: jest.fn(), useAudioRecorderState: jest.fn(),
}));
jest.mock("expo-file-system", () => {
  const files = new Map();
  class File {
    static downloadFileAsync = jest.fn();
    constructor(directory, name) { this.uri = directory + "/" + name; }
    get exists() { return files.has(this.uri); }
    textSync() { return files.get(this.uri); }
    write(text) { files.set(this.uri, text); }
    delete() { files.delete(this.uri); }
  }
  return { File, Paths: { document: "file:///documents", cache: "file:///cache" } };
});
