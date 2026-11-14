import { fireEvent, render, screen } from "@testing-library/react-native";
import { createElement } from "react";
import * as SecureStore from "expo-secure-store";
import * as ImagePicker from "expo-image-picker";
import * as DocumentPicker from "expo-document-picker";
import * as Sharing from "expo-sharing";
import { File, Paths } from "expo-file-system";
import { requestRecordingPermissionsAsync, useAudioPlayer, useAudioPlayerStatus, useAudioRecorder, useAudioRecorderState, setAudioModeAsync } from "expo-audio";
import { AppState } from "react-native";
import App from "../App";
import { api } from "../src/api";
import type { AcceptedAnalysis, Analysis, AnalysisStatus, Bird } from "../../shared/contracts";
import type { Entitlement } from "../../shared/subscriptions";

export const profile = { id: "mobile-qa", email: "mobile@example.test", createdAt: "2026-10-30T12:00:00Z" };
export const token = "mobile-fixture-session";
export const bird: Bird = {
  id: 11, ukrainianName: "Кардинал червоний", commonName: "Northern Cardinal",
  scientificName: "Cardinalis cardinalis", family: "Cardinalidae", habitat: "Ліс",
  distribution: "", diet: "Насіння", averageSize: "", description: "", conservationStatus: "",
  sourceUrl: "https://example.test/birds", sources: ["https://example.test/birds"],
};
export const photo: Analysis = {
  id: 7, analysisId: "mobile-photo", analysisType: "image", status: "Completed",
  originalFileName: "cardinal.png", fileUrl: "/api/analyses/7/file", predictedSpecies: "Cardinal",
  bird, confidence: .8, modelName: "fixture-vit", modelRevision: "test-revision", elapsedSeconds: .45,
  analysisDate: profile.createdAt, description: "Модельне спостереження",
  predictions: [
    { rank: 1, speciesName: "Cardinal", scientificName: bird.scientificName, confidence: .8 },
    { rank: 2, speciesName: "Blue Jay", scientificName: null, confidence: .15 },
    { rank: 3, speciesName: "Mallard", scientificName: null, confidence: .05 },
  ],
  evaluation: { expectedLabel: "Cardinal", expectedScientific: bird.scientificName,
    labelSource: "https://example.test/reference", top1Correct: true, top3Correct: false, experiment: "fixture" },
};
export const audio: Analysis = {
  ...photo, id: 8, analysisId: "mobile-audio", analysisType: "audio", originalFileName: "song.wav",
  fileUrl: "/api/analyses/8/file", predictedSpecies: "Background", bird: null, evaluation: null,
};
export const access: Entitlement = {
  planId: 2, plan: "PREMIUM", status: "Active", analysisLimit: 500, analysesUsed: 2, remaining: 498,
  features: ["image", "audio", "history", "gallery", "statistics", "advancedStatistics", "compare", "batch"],
  startDate: profile.createdAt, endDate: "2026-11-30T12:00:00Z", subscriptionId: "mobile-period",
};
export const summary = { totalAnalyses: 2, imageAnalyses: 1, audioAnalyses: 1, identifiedSpecies: 1,
  averageConfidence: .8, minConfidence: .7, maxConfidence: .9, labelledEvaluations: 1 };
export const task: AcceptedAnalysis = { analysisId: photo.analysisId, historyId: photo.id, status: "Pending",
  statusUrl: "/api/analyses/mobile-photo/status" };
export function state(status: AnalysisStatus["status"], extra: Partial<AnalysisStatus> = {}): AnalysisStatus {
  return {
    analysisId: task.analysisId, historyId: task.historyId, analysisType: "image", status,
    createdAt: profile.createdAt, retryCount: 0, correlationId: "mobile-test-correlation",
    stalled: false, startedAt: null, completedAt: null, errorMessage: null, result: null, ...extra,
  };
}
export function page(items: Analysis[], total = items.length) {
  return { items, total, page: 1, pageSize: 15 };
}
export const json = (body: unknown, status = 200) => ({
  ok: status >= 200 && status < 300, status, json: async () => body,
}) as Response;
type Handler = (url: URL, init?: RequestInit) => Response | Promise<Response> | undefined;
export function mockHttp(handle: Handler = () => undefined) {
  const request = jest.spyOn(global, "fetch").mockImplementation(async (input, init) => {
    const url = new URL(String(input));
    const response = handle(url, init);
    if (response) return response;
    switch (url.pathname) {
      case "/api/auth/me": return json(profile);
      case "/api/auth/logout": return json(null, 204);
      case "/api/health": return json({ status: "ok" });
      case "/api/statistics/summary": return json(summary);
      case "/api/statistics/species": return json([{ species: "Cardinal", count: 2 }]);
      case "/api/statistics/confidence": return json([{ label: "75–100%", count: 2 }]);
      case "/api/statistics/evaluations": return json([{ model: "fixture-vit", experiment: "original", samples: 2, top1Correct: 1, top3Correct: 2 }]);
      case "/api/subscriptions/me": return json(access);
      case "/api/subscriptions/payments":
      case "/api/subscriptions/history": return json([]);
      case "/api/analyses": return json(page([photo, audio]));
      case "/api/analyses/gallery": return json(page([photo]));
      case "/api/analyses/7": return json(photo);
      case "/api/analyses/8": return json(audio);
      case "/api/birds/11": return json(bird);
      default: return json({ detail: "Unexpected test request " + url.pathname }, 500);
    }
  });
  return request;
}
export const player = { play: jest.fn(), pause: jest.fn(), seekTo: jest.fn() };
export const recorder = { prepareToRecordAsync: jest.fn(), record: jest.fn(), stop: jest.fn(), uri: "file:///song.m4a" };
export function resetNative() {
  for (const native of [
    SecureStore.getItemAsync, SecureStore.setItemAsync, SecureStore.deleteItemAsync,
    ImagePicker.requestCameraPermissionsAsync, ImagePicker.requestMediaLibraryPermissionsAsync,
    ImagePicker.launchCameraAsync, ImagePicker.launchImageLibraryAsync, DocumentPicker.getDocumentAsync,
    Sharing.isAvailableAsync, Sharing.shareAsync, File.downloadFileAsync, requestRecordingPermissionsAsync,
    useAudioPlayer, useAudioPlayerStatus, useAudioRecorder, useAudioRecorderState, setAudioModeAsync,
    ...Object.values(player), recorder.prepareToRecordAsync, recorder.record, recorder.stop,
  ]) {
    if (jest.isMockFunction(native)) native.mockReset();
  }
  api.setAccessToken(null);
  jest.mocked(SecureStore.getItemAsync).mockResolvedValue(token);
  jest.mocked(useAudioPlayer).mockReturnValue(player as never);
  jest.mocked(useAudioPlayerStatus).mockReturnValue({ playing: false, didJustFinish: false, currentTime: 0, duration: 1 } as never);
  jest.mocked(useAudioRecorder).mockReturnValue(recorder as never);
  jest.mocked(useAudioRecorderState).mockReturnValue({ isRecording: false, durationMillis: 0 } as never);
  jest.mocked(setAudioModeAsync).mockResolvedValue(undefined);
  new File(Paths.document, "bird-task-image.json").delete();
  new File(Paths.document, "bird-task-audio.json").delete();
  AppState.currentState = "active";
}
export async function signedIn() {
  const view = render(createElement(App));
  await screen.findByText("Світ птахів поруч.");
  return view;
}
export function press(name: string | RegExp) {
  fireEvent.press(screen.getByRole("button", { name }));
}
