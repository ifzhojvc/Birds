import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react-native";
import { Alert, FlatList, Linking } from "react-native";
import { File } from "expo-file-system";
import { useAudioPlayer, useAudioPlayerStatus } from "expo-audio";
import * as Sharing from "expo-sharing";
import * as SecureStore from "expo-secure-store";
import { access, audio, bird, json, mockHttp, page, photo, player, press, resetNative, signedIn, token } from "./fixtures";

beforeEach(resetNative);
afterEach(() => {
  cleanup();
  jest.restoreAllMocks();
});

it("navigates from home to history, filters and pages without dropping bearer authentication", async () => {
  const fetch = mockHttp((url) => {
    if (url.pathname === "/api/analyses" && url.searchParams.has("page")) {
      return json(page(url.searchParams.get("analysisType") === "audio" ? [audio] : [photo, audio], 32));
    }
  });
  await signedIn();
  expect(await screen.findByText(bird.ukrainianName)).toBeOnTheScreen();
  press("Усі");
  expect(await screen.findByText("Знайдено: 32 · Сторінка 1")).toBeOnTheScreen();
  press("Далі");
  expect(await screen.findByText("Знайдено: 32 · Сторінка 2")).toBeOnTheScreen();
  press("Назад");
  fireEvent.press(screen.getAllByRole("button", { name: "Аудіо" })[0]);
  press("Нові → старі");
  press("Старі → нові");
  press("За confidence");
  press("Нові → старі");
  fireEvent.changeText(screen.getByLabelText("Пошук у спільній історії"), "song");
  press("Оновити");
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    expect.stringContaining("page=1&pageSize=15&search=song&analysisType=audio&sort=date_asc"),
    expect.objectContaining({ headers: { Authorization: "Bearer " + token } }),
  ));
  fireEvent.press((await screen.findAllByText(audio.predictedSpecies))[0]);
  expect(await screen.findByText("Перевіреної довідки для цього класу немає.")).toBeOnTheScreen();
  expect(useAudioPlayer).toHaveBeenCalledWith({
    uri: "http://birdlab.test:5100/api/analyses/8/file", headers: { Authorization: "Bearer " + token },
  });
  jest.mocked(useAudioPlayerStatus).mockReturnValue({ playing: false, didJustFinish: true, currentTime: 1, duration: 1 } as never);
  press("Відтворити запис");
  expect(player.play).toHaveBeenCalled();
});

it("recovers history pagination after a failed load and refreshes with bearer authentication", async () => {
  let loads = 0;
  const request = mockHttp((url) => {
    if (url.pathname === "/api/analyses" && url.searchParams.has("page")) {
      loads++;
      if (loads === 1) return json({ detail: "Історія тимчасово недоступна" }, 503);
      return json(loads === 2 ? page([photo], 16) : page([audio]));
    }
  });
  await signedIn();
  press("Усі");
  expect(await screen.findByRole("alert")).toHaveTextContent("Історія тимчасово недоступна");
  expect(screen.queryByRole("button", { name: "Назад" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Далі" })).toBeNull();
  press("Спробувати ще раз");
  expect(await screen.findByText("Знайдено: 16 · Сторінка 1")).toBeOnTheScreen();
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.getByRole("button", { name: "Назад" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Далі" })).toBeEnabled();
  fireEvent(screen.UNSAFE_getByType(FlatList), "refresh");
  expect(await screen.findByText("Знайдено: 1 · Сторінка 1")).toBeOnTheScreen();
  expect((await screen.findAllByText(audio.predictedSpecies))[0]).toBeOnTheScreen();
  expect(screen.getByRole("button", { name: "Далі" })).toBeDisabled();
  const historyRequests = request.mock.calls.filter(([url]) => String(url).includes("/api/analyses?page="));
  expect(historyRequests).toHaveLength(3);
  for (const [, init] of historyRequests) {
    expect(init?.headers).toEqual({ Authorization: "Bearer " + token });
  }
});

it("handles gallery details, reference links and a confirmed delete after a recoverable failure", async () => {
  let deletions = 0;
  const request = mockHttp((url, init) => {
    if (url.pathname === "/api/analyses/7" && init?.method === "DELETE") {
      return ++deletions === 1 ? json({ detail: "Файл тимчасово зайнятий" }, 503) : json(null, 204);
    }
    if (url.pathname === "/api/analyses" && deletions === 2) return json(page([]));
  });
  const alert = jest.spyOn(Alert, "alert").mockImplementation(() => {});
  const link = jest.spyOn(Linking, "openURL").mockResolvedValue(undefined);
  const view = await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Галерея");
  expect(await screen.findByText("Галерея птахів")).toBeOnTheScreen();
  fireEvent.press(await screen.findByText(bird.ukrainianName));
  expect(await screen.findByText("Еталон: Cardinal")).toBeOnTheScreen();
  press("Картка виду");
  expect(await screen.findByText("Перевірена довідкова інформація та джерела.")).toBeOnTheScreen();
  fireEvent.press(screen.getByRole("link", { name: /Джерело 1/ }));
  expect(link).toHaveBeenCalledWith(bird.sources[0]);
  view.unmount();
  await signedIn();
  fireEvent.press(await screen.findByText(bird.ukrainianName));
  await screen.findByText("Еталон: Cardinal");
  press("Видалити аналіз");
  expect(alert.mock.calls[0][2]?.[0]).toMatchObject({ text: "Скасувати", style: "cancel" });
  expect(deletions).toBe(0);
  await act(async () => alert.mock.calls[0][2]?.[1].onPress?.());
  expect(await screen.findByText("Файл тимчасово зайнятий")).toBeOnTheScreen();
  press("Видалити аналіз");
  await act(async () => alert.mock.calls[1][2]?.[1].onPress?.());
  expect(await screen.findByText("Моя історія")).toBeOnTheScreen();
  expect(await screen.findByText("Нове відкриття попереду")).toBeOnTheScreen();
  expect(request.mock.calls.filter(([, init]) => init?.method === "DELETE")).toHaveLength(2);
});

it("retries statistics after an API error and displays reference counts", async () => {
  let available = false;
  mockHttp((url) => url.pathname === "/api/statistics/species" && !available ? json({ detail: "Статистика недоступна" }, 503) : undefined);
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Статистика");
  expect(await screen.findByRole("alert")).toHaveTextContent("Статистика недоступна");
  available = true;
  press("Спробувати ще раз");
  expect(await screen.findByText(/original: 2 зразків/)).toHaveTextContent(/TOP-1 1\/2, TOP-3 2\/2/);
  expect(screen.getByText("75–100% · 2 аналізів")).toBeOnTheScreen();
});

it("does not request reference statistics for a limited plan", async () => {
  const request = mockHttp((url) => url.pathname === "/api/subscriptions/me"
    ? json({ ...access, features: ["statistics"] }) : undefined);
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Статистика");
  expect(await screen.findByText("Немає оцінених еталонних зразків.")).toBeOnTheScreen();
  expect(request.mock.calls.some(([url]) => String(url).includes("/statistics/evaluations"))).toBe(false);
});

it("uses modal search and prevents comparison of identical selections", async () => {
  const jay = { ...bird, id: 12, ukrainianName: "", commonName: "Blue Jay", scientificName: "Cyanocitta cristata", family: "" };
  const request = mockHttp((url) => {
    if (url.pathname === "/api/birds") return json([bird, jay]);
    if (url.pathname === "/api/birds/compare") return json({ first: bird, second: jay });
    if (url.pathname === "/api/analyses/compare") return json({ first: photo, second: audio, conclusion: "Потрібна перевірка таксонів" });
  });
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Порівняння видів і моделей");
  const cardinal = bird.ukrainianName + " · " + bird.scientificName;
  fireEvent.press((await screen.findAllByRole("button", { name: "Оберіть зі списку" }))[0]);
  fireEvent.changeText(screen.getByPlaceholderText("Пошук…"), "unknown");
  expect(await screen.findByText("Нічого не знайдено.")).toBeOnTheScreen();
  fireEvent.changeText(screen.getByPlaceholderText("Пошук…"), "cardinalis");
  fireEvent.press((await screen.findAllByText(cardinal)).at(-1)!);
  fireEvent.press(screen.getAllByRole("button", { name: "Оберіть зі списку" })[0]);
  fireEvent.press((await screen.findAllByText(cardinal)).at(-1)!);
  expect(screen.getByText("Оберіть різні види.")).toBeOnTheScreen();
  expect(request.mock.calls.some(([url]) => String(url).includes("/birds/compare"))).toBe(false);
  fireEvent.press(screen.getAllByRole("button", { name: cardinal })[1]);
  fireEvent.press(await screen.findByText("Blue Jay · Cyanocitta cristata"));
  expect(await screen.findByText("Cardinalidae")).toBeOnTheScreen();
  const photoLabel = "№7 · Фото · " + bird.ukrainianName;
  fireEvent.press(screen.getAllByRole("button", { name: "Оберіть зі списку" })[0]);
  press("Закрити");
  fireEvent.press(screen.getAllByRole("button", { name: "Оберіть зі списку" })[0]);
  fireEvent.press((await screen.findAllByText(photoLabel)).at(-1)!);
  press("Оберіть зі списку");
  fireEvent.press((await screen.findAllByText(photoLabel)).at(-1)!);
  expect(screen.getByText("Оберіть різні аналізи.")).toBeOnTheScreen();
  fireEvent.press(screen.getAllByRole("button", { name: photoLabel })[1]);
  fireEvent.press(await screen.findByText("№8 · Аудіо · Background"));
  expect(await screen.findByText("Потрібна перевірка таксонів")).toBeOnTheScreen();
});

it("shares authorized CSV only after confirming native sharing availability", async () => {
  let available = false;
  mockHttp((url) => url.pathname === "/api/health" && !available ? json({ detail: "Worker недоступний" }, 503) : undefined);
  jest.mocked(Sharing.isAvailableAsync).mockResolvedValueOnce(false).mockResolvedValue(true);
  jest.mocked(File.downloadFileAsync).mockResolvedValue({ uri: "file:///cache/bird_analysis.csv" } as File);
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  expect(await screen.findByText("Worker недоступний")).toBeOnTheScreen();
  available = true;
  press("Перевірити з’єднання");
  expect(await screen.findByText("Сервіси доступні")).toBeOnTheScreen();
  press("Експортувати CSV");
  expect(await screen.findByText(/Спільний доступ до файлів недоступний/)).toBeOnTheScreen();
  expect(File.downloadFileAsync).not.toHaveBeenCalled();
  press("Експортувати CSV");
  await waitFor(() => expect(Sharing.shareAsync).toHaveBeenCalledWith(
    "file:///cache/bird_analysis.csv", expect.objectContaining({ mimeType: "text/csv" }),
  ));
  expect(File.downloadFileAsync).toHaveBeenCalledWith("http://birdlab.test:5100/api/export/csv",
    expect.objectContaining({ uri: "file:///cache/bird_analysis.csv" }),
    { idempotent: true, headers: { Authorization: "Bearer " + token } },
  );
});

it("shows the subscription and recorded payments, then logs out locally during an outage", async () => {
  let snapshots = 0;
  mockHttp((url) => {
    if (url.pathname === "/api/subscriptions/me") return json(++snapshots === 1 ? access : {
      ...access, planId: 0, plan: "FREE", features: ["image"], analysisLimit: 5, remaining: 4, analysesUsed: 1,
    });
    if (url.pathname === "/api/subscriptions/payments") return json([
      { id: "payment-a", subscriptionPlanId: 2, amount: "10000000", tokenDecimals: 6,
        status: "Pending", blockchainNetwork: "Local Anvil", createdAt: access.startDate,
        transactionHash: "0x123", error: "Очікуємо ще один блок" },
    ]);
    if (url.pathname === "/api/subscriptions/history") return json([{ ...access, id: "period", planId: 1, status: "Expired" }]);
    if (url.pathname === "/api/auth/logout") return json({ detail: "API недоступний" }, 503);
  });
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Мій тариф і платежі");
  expect(await screen.findByText("PREMIUM")).toBeOnTheScreen();
  expect(await screen.findByText("Очікуємо ще один блок")).toBeOnTheScreen();
  expect(await screen.findByText("PRO · Строк минув")).toBeOnTheScreen();
  press("Оновити підписку з сервера");
  expect(await screen.findByText("FREE")).toBeOnTheScreen();
  press("Вийти з облікового запису");
  expect(await screen.findByRole("alert")).toHaveTextContent(/Вихід на пристрої виконано/);
  expect(SecureStore.deleteItemAsync).toHaveBeenCalledWith("birdlab.auth.session");
});


it("shows preserved PRO rights and historical payments without a new confirmation", async () => {
  mockHttp((url) => {
    if (url.pathname === "/api/subscriptions/me") return json({
      ...access, planId: 1, plan: "PRO", analysisLimit: 100, analysesUsed: 1, remaining: 99,
      endDate: "2026-12-16T18:00:28.314206Z", accessSource: "LegacyLocalSnapshot", verificationState: "ArchivedUnverified",
    });
    if (url.pathname === "/api/subscriptions/payments") return json([{
      id: "historical-pro", subscriptionPlanId: 1, amount: "5000000", tokenDecimals: 6,
      status: "Confirmed", blockchainNetwork: "Local Anvil", createdAt: access.startDate,
      transactionHash: "0xhistorical-payment", isHistorical: true, verificationState: "ArchivedUnverified",
    }]);
  });
  await signedIn();
  fireEvent.press(screen.getByText("Інформація"));
  press("Мій тариф і платежі");
  expect(await screen.findByText(/Права збережено з історичної локальної оплати/)).toBeOnTheScreen();
  expect(screen.getByText(/залишилось 99/)).toBeOnTheScreen();
  expect(await screen.findByText("Історичний статус: Підтверджено сервером")).toBeOnTheScreen();
  expect(screen.getByText(/У поточній мережі ця транзакція не підтверджувалася/)).toBeOnTheScreen();
  expect(screen.getByText("0xhistorical-payment")).toBeOnTheScreen();
});
