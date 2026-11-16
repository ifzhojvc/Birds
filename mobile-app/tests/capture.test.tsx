import { act, cleanup, screen, waitFor } from "@testing-library/react-native";
import { Alert, AppState, Linking, type AppStateStatus } from "react-native";
import * as ImagePicker from "expo-image-picker";
import * as DocumentPicker from "expo-document-picker";
import { File, Paths } from "expo-file-system";
import { requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorderState } from "expo-audio";
import { audio, json, mockHttp, photo, press, recorder, resetNative, signedIn, state, task } from "./fixtures";

beforeEach(resetNative);
afterEach(() => {
  cleanup();
  jest.restoreAllMocks();
});

const asset = { uri: "file:///qa/bird.png", width: 20, height: 20, mimeType: "image/png", fileSize: 100 };
const granted = { granted: true, status: "granted", canAskAgain: true, expires: "never" } as const;
const denied = { ...granted, granted: false, status: "denied" } as const;
const pickPhoto = () => {
  jest.mocked(ImagePicker.requestMediaLibraryPermissionsAsync).mockResolvedValue(granted as never);
  jest.mocked(ImagePicker.launchImageLibraryAsync).mockResolvedValue({ canceled: false, assets: [asset] });
  press("Обрати з галереї");
};

it("offers Settings on camera denial, handles picker cancellation and rejects HEIC", async () => {
  mockHttp();
  const alert = jest.spyOn(Alert, "alert").mockImplementation(() => {});
  const settings = jest.spyOn(Linking, "openSettings").mockResolvedValue(undefined);
  jest.mocked(ImagePicker.requestCameraPermissionsAsync).mockResolvedValue(denied as never);
  await signedIn();
  press("Аналізувати фото");
  press("Зробити фото");
  await waitFor(() => expect(alert).toHaveBeenCalledWith("Потрібен дозвіл", expect.any(String), expect.any(Array)));
  alert.mock.calls[0][2]?.[1].onPress?.();
  expect(settings).toHaveBeenCalled();
  expect(ImagePicker.launchCameraAsync).not.toHaveBeenCalled();
  jest.mocked(ImagePicker.requestCameraPermissionsAsync).mockResolvedValue(granted as never);
  jest.mocked(ImagePicker.launchCameraAsync).mockResolvedValueOnce({ canceled: true, assets: null });
  press("Зробити фото");
  await waitFor(() => expect(ImagePicker.launchCameraAsync).toHaveBeenCalled());
  expect(screen.queryByLabelText("Вибране фото")).toBeNull();
  jest.mocked(ImagePicker.launchCameraAsync).mockResolvedValue({ canceled: false, assets: [{ ...asset, mimeType: "image/heic" }] });
  press("Зробити фото");
  expect(await screen.findByRole("alert")).toHaveTextContent(/Оберіть JPEG або PNG/);
});

it("rejects oversized photos and handles a native picker exception", async () => {
  const request = mockHttp();
  await signedIn();
  press("Аналізувати фото");
  jest.mocked(ImagePicker.requestMediaLibraryPermissionsAsync).mockResolvedValue(granted as never);
  jest.mocked(ImagePicker.launchImageLibraryAsync).mockResolvedValueOnce({ canceled: false, assets: [{ ...asset, fileSize: 21 * 1024 * 1024 }] });
  press("Обрати з галереї");
  await screen.findByLabelText("Вибране фото");
  press("Проаналізувати фото");
  expect(await screen.findByRole("alert")).toHaveTextContent(/Файл перевищує ліміт/);
  expect(request.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  jest.mocked(ImagePicker.launchImageLibraryAsync).mockRejectedValueOnce(new Error("picker failed"));
  press("Обрати з галереї");
  expect(await screen.findByRole("alert")).toHaveTextContent(/Не вдалося відкрити камеру або галерею/);
});

it("uploads one photo, pauses polling in background and resumes without another POST", async () => {
  let current: (value: AppStateStatus) => void = () => undefined;
  const remove = jest.fn();
  jest.spyOn(AppState, "addEventListener").mockImplementation((_, listener) => {
    current = listener as (value: AppStateStatus) => void;
    return { remove };
  });
  let pollSignal: AbortSignal | null = null;
  let polls = 0;
  const request = mockHttp((url, init) => {
    if (url.pathname === "/api/analysis/image") {
      const parts = (init?.body as FormData & { getParts(): Array<{ uri: string; fieldName: string }> }).getParts();
      expect(parts).toEqual([expect.objectContaining({ uri: asset.uri, fieldName: "file" })]);
      return json(task, 202);
    }
    if (url.pathname === task.statusUrl) {
      if (++polls === 1) {
        pollSignal = init?.signal || null;
        return new Promise((_, reject) => pollSignal?.addEventListener("abort", () => reject(new Error("aborted"))));
      }
      return json(state("Completed", { result: photo }));
    }
  });
  const view = await signedIn();
  press("Аналізувати фото");
  pickPhoto();
  await screen.findByLabelText("Вибране фото");
  press("Проаналізувати фото");
  expect(await screen.findByText("Очікування в черзі")).toBeOnTheScreen();
  await waitFor(() => expect(pollSignal).not.toBeNull());
  await act(async () => current("background"));
  expect((pollSignal as AbortSignal | null)?.aborted).toBe(true);
  expect(polls).toBe(1);
  await act(async () => current("active"));
  expect(await screen.findByText("3. Mallard")).toBeOnTheScreen();
  expect(screen.getByText("2. Blue Jay")).toBeOnTheScreen();
  press("Відкрити результат в історії");
  expect(await screen.findByText("Аналіз № 7")).toBeOnTheScreen();
  expect(request.mock.calls.filter(([url]) => String(url).includes("/api/analysis/image"))).toHaveLength(1);
  expect(new File(Paths.document, "bird-task-image.json").textSync()).toContain(task.analysisId);
  view.unmount();
  expect(remove).toHaveBeenCalled();
});

it("keeps tracking an accepted photo when saving its task ID fails", async () => {
  const save = jest.spyOn(File.prototype, "writeSync").mockImplementationOnce(() => {
    throw new Error("Device storage unavailable");
  });
  let complete: ((value: Response) => void) | undefined;
  const request = mockHttp((url) => {
    if (url.pathname === "/api/analysis/image") return json(task, 202);
    if (url.pathname === task.statusUrl) return new Promise<Response>((resolve) => {
      complete = resolve;
    });
  });
  await signedIn();
  press("Аналізувати фото");
  pickPhoto();
  await screen.findByLabelText("Вибране фото");
  press("Проаналізувати фото");
  expect(await screen.findByRole("alert")).toHaveTextContent(/ID не вдалося зберегти на пристрої/);
  expect(screen.getByText(`Завдання ${task.analysisId}`)).toBeOnTheScreen();
  expect(save).toHaveBeenCalledWith(JSON.stringify({ base: "http://birdlab.test:5100", task }));
  expect(new File(Paths.document, "bird-task-image.json").exists).toBe(false);
  await waitFor(() => expect(complete).toBeDefined());
  await act(async () => {
    complete?.(json(state("Completed", { result: photo })));
  });
  expect(await screen.findByText("3. Mallard")).toBeOnTheScreen();
  expect(request.mock.calls.filter(([url]) => String(url).includes("/api/analysis/image"))).toHaveLength(1);
});

it("aborts an in-flight upload on unmount", async () => {
  let uploadSignal: AbortSignal | null = null;
  mockHttp((url, init) => {
    if (url.pathname === "/api/analysis/image") {
      uploadSignal = init?.signal || null;
      return new Promise((_, reject) => uploadSignal?.addEventListener("abort", () => reject(new Error("aborted"))));
    }
  });
  const view = await signedIn();
  press("Аналізувати фото");
  pickPhoto();
  await screen.findByLabelText("Вибране фото");
  press("Проаналізувати фото");
  expect(await screen.findByRole("button", { name: "Завантажуємо фотографію…" })).toBeDisabled();
  expect(uploadSignal).not.toBeNull();
  view.unmount();
  expect((uploadSignal as AbortSignal | null)?.aborted).toBe(true);
});

it("restores a saved task and shows both delayed and terminal worker states", async () => {
  new File(Paths.document, "bird-task-image.json").writeSync(JSON.stringify({ base: "http://birdlab.test:5100", task }));
  let polls = 0;
  const request = mockHttp((url) => {
    if (url.pathname === task.statusUrl) return ++polls === 1
      ? json(state("Processing", { stalled: true })) : json(state("Failed", { errorMessage: "Модель недоступна" }));
  });
  await signedIn();
  press("Аналізувати фото");
  expect(await screen.findByText(/Тривале очікування/)).toBeOnTheScreen();
  expect(await screen.findByRole("alert")).toHaveTextContent("Модель недоступна");
  expect(request.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
});

it("imports an audio file, surfaces a rejected upload, and plays the successful retry result", async () => {
  let attempts = 0;
  mockHttp((url) => {
    if (url.pathname === "/api/analysis/audio") return ++attempts === 1 ? json({ detail: "Потрібен тариф PRO" }, 403) : json(task, 202);
    if (url.pathname === task.statusUrl) return json(state("Completed", { analysisType: "audio", result: audio }));
  });
  jest.mocked(DocumentPicker.getDocumentAsync).mockRejectedValueOnce(new Error("picker failed"));
  jest.mocked(DocumentPicker.getDocumentAsync).mockResolvedValue({ canceled: false, assets: [{ name: "song.wav", uri: "file:///song.wav", size: 1024, lastModified: 0 }] });
  await signedIn();
  press("Розпізнати голос");
  press("Обрати аудіофайл");
  expect(await screen.findByRole("alert")).toHaveTextContent(/Не вдалося обрати аудіофайл/);
  press("Обрати аудіофайл");
  expect(await screen.findByText("song.wav")).toBeOnTheScreen();
  press("Проаналізувати аудіо");
  expect(await screen.findByRole("alert")).toHaveTextContent("Потрібен тариф PRO");
  press("Проаналізувати аудіо");
  expect(await screen.findByText("Перевіреної довідки для цього класу немає.")).toBeOnTheScreen();
  expect(attempts).toBe(2);
});

it("honors microphone permission and closes recording mode after stopping", async () => {
  mockHttp();
  const alert = jest.spyOn(Alert, "alert").mockImplementation(() => {});
  jest.mocked(requestRecordingPermissionsAsync).mockResolvedValueOnce(denied as never).mockResolvedValue(granted as never);
  jest.mocked(recorder.prepareToRecordAsync).mockResolvedValue(undefined);
  jest.mocked(recorder.stop).mockResolvedValue(undefined);
  await signedIn();
  press("Розпізнати голос");
  press("Записати з мікрофона");
  await waitFor(() => expect(alert).toHaveBeenCalled());
  expect(recorder.prepareToRecordAsync).not.toHaveBeenCalled();
  jest.mocked(recorder.record).mockImplementation(() => {
    jest.mocked(useAudioRecorderState).mockReturnValue({ isRecording: true, durationMillis: 1300 } as never);
  });
  press("Записати з мікрофона");
  expect(await screen.findByRole("button", { name: "Зупинити · 1 с" })).toBeEnabled();
  jest.mocked(recorder.stop).mockImplementation(async () => {
    jest.mocked(useAudioRecorderState).mockReturnValue({ isRecording: false, durationMillis: 1300 } as never);
  });
  press("Зупинити · 1 с");
  expect(await screen.findByRole("button", { name: "Проаналізувати аудіо" })).toBeEnabled();
  expect(setAudioModeAsync).toHaveBeenCalledWith({ allowsRecording: false, playsInSilentMode: true });
});

it.each([
  ["image", "На зображенні не вдалося виявити птаха. Спробуйте завантажити іншу фотографію."],
  ["image", "Не вдалося впевнено визначити, чи є на фото птах. Спробуйте чіткіше зображення."],
  ["audio", "На аудіозаписі не вдалося виявити звуків птахів. Завантажте інший запис."],
  ["audio", "Не вдалося впевнено визначити наявність голосу птаха. Спробуйте якісніший запис."],
  ["audio", "Звуки птаха виявлено, але визначити вид із достатньою впевненістю не вдалося."],
] as const)("shows a terminal %s content rejection without a species result: %s", async (kind, message) => {
  new File(Paths.document, `bird-task-${kind}.json`).writeSync(JSON.stringify({ base: "http://birdlab.test:5100", task }));
  const request = mockHttp((url) => {
    if (url.pathname === task.statusUrl) return json(state("Failed", { analysisType: kind, errorMessage: message }));
  });
  await signedIn();
  press(kind === "image" ? "Аналізувати фото" : "Розпізнати голос");
  expect(await screen.findByRole("alert")).toHaveTextContent(message);
  expect(screen.queryByText("3. Mallard")).toBeNull();
  expect(screen.queryByText("Відкрити результат в історії")).toBeNull();
  expect(request.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
});
