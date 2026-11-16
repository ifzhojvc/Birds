import "@testing-library/jest-dom/vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { AcceptedAnalysis, Analysis, AnalysisStatus, Bird } from "../../shared/contracts";
import type { Entitlement } from "../../shared/subscriptions";
import App from "./App";
import { api } from "./api";
import { AuthProvider } from "./auth";

const bird: Bird = {
  id: 11, ukrainianName: "Кардинал червоний", commonName: "Northern Cardinal",
  scientificName: "Cardinalis cardinalis", family: "Cardinalidae", habitat: "Ліс",
  distribution: "", diet: "Насіння", averageSize: "", description: "", conservationStatus: "",
  sourceUrl: "https://example.test/birds", sources: ["https://example.test/birds"],
};
const photo: Analysis = {
  id: 7, analysisId: "test-photo", analysisType: "image", status: "Completed",
  originalFileName: "cardinal.png", fileUrl: "/api/analyses/7/file", predictedSpecies: "Cardinal",
  bird, confidence: .8, modelName: "fixture-vit", modelRevision: "abc", elapsedSeconds: .45,
  analysisDate: "2026-10-30T12:00:00Z", description: "Модельне спостереження",
  predictions: [
    { rank: 1, speciesName: "Cardinal", scientificName: bird.scientificName, confidence: .8 },
    { rank: 2, speciesName: "Blue Jay", scientificName: null, confidence: .15 },
    { rank: 3, speciesName: "Mallard", scientificName: null, confidence: .05 },
  ],
  evaluation: { expectedLabel: "Cardinal", expectedScientific: bird.scientificName,
    labelSource: "https://example.test/reference", top1Correct: true, top3Correct: true, experiment: "fixture" },
};
const audio: Analysis = {
  ...photo, id: 8, analysisId: "test-audio", analysisType: "audio", originalFileName: "song.wav",
  fileUrl: "/api/analyses/8/file", predictedSpecies: "Background", bird: null, evaluation: null,
};
const access: Entitlement = {
  planId: 2, plan: "PREMIUM", status: "Active", analysisLimit: 500, analysesUsed: 2, remaining: 498,
  features: ["image", "audio", "history", "gallery", "statistics", "advancedStatistics", "compare", "batch"],
  startDate: "2026-10-30T12:00:00Z", endDate: "2026-11-30T12:00:00Z", subscriptionId: "fixture-period",
};
const profile = { id: "web-qa", email: "birdwatcher@example.test" };
const summary = { totalAnalyses: 2, imageAnalyses: 1, audioAnalyses: 1, identifiedSpecies: 1,
  averageConfidence: .8, minConfidence: .7, maxConfidence: .9, labelledEvaluations: 1 };
const task: AcceptedAnalysis = {
  analysisId: photo.analysisId, historyId: photo.id, status: "Pending",
  statusUrl: "/api/analyses/test-photo/status",
};
function status(state: AnalysisStatus["status"], values: Partial<AnalysisStatus> = {}): AnalysisStatus {
  return {
    analysisId: task.analysisId, historyId: task.historyId, analysisType: "image", status: state,
    createdAt: "2026-10-30T12:00:00Z", retryCount: 0, correlationId: "test-correlation",
    stalled: false, startedAt: null, completedAt: null, result: null, errorMessage: null, ...values,
  };
}
function page(items: Analysis[], total = items.length, pageSize = 15) {
  return { items, total, page: 1, pageSize };
}
const json = (body: unknown, statusCode = 200) => Response.json(body, { status: statusCode });
type Handler = (url: URL, init?: RequestInit) => Response | Promise<Response> | undefined;
function mockHttp(handle: Handler = () => undefined) {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const response = handle(url, init);
    if (response) return response;
    switch (url.pathname) {
      case "/api/auth/me": return json(profile);
      case "/api/health": return json({ status: "ok" });
      case "/api/subscriptions/me": return json(access);
      case "/api/subscriptions/history": return json([]);
      case "/api/subscriptions/plans": return json([]);
      case "/api/statistics/summary": return json(summary);
      case "/api/statistics/species": return json([{ species: "Cardinal", count: 2, averageConfidence: .8 }]);
      case "/api/statistics/confidence": return json([{ label: "75–100%", count: 2 }]);
      case "/api/statistics/evaluations": return json([{ model: "fixture-vit", experiment: "original", samples: 2, top1Correct: 1, top3Correct: 2 }]);
      case "/api/analyses": return json(page([photo, audio]));
      case "/api/analyses/gallery": return json(page([photo]));
      case "/api/analyses/7": return json(photo);
      case "/api/analyses/8": return json(audio);
      case "/api/birds/11": return json(bird);
      default: return json({ detail: "Unexpected test request " + url.pathname }, 500);
    }
  });
  vi.stubGlobal("fetch", request);
  return request;
}

const createObjectURL = vi.fn(() => "blob:bird-preview");
const revokeObjectURL = vi.fn();
beforeEach(() => {
  localStorage.clear();
  vi.stubEnv("VITE_POLL_INTERVAL_MS", "500");
  vi.stubGlobal("ResizeObserver", class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect")
    .mockReturnValue(new DOMRect(0, 0, 640, 320));
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: createObjectURL },
    revokeObjectURL: { configurable: true, value: revokeObjectURL },
  });
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.clearAllMocks();
});

function mount(path: string) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0, gcTime: 0 } } });
  const view = render(<QueryClientProvider client={cache}><MemoryRouter initialEntries={[path]}>
    <AuthProvider><App /></AuthProvider>
  </MemoryRouter></QueryClientProvider>);
  return { ...view, user: userEvent.setup() };
}
const file = (name = "bird.png") => new File(["bird"], name, { type: name.endsWith(".wav") ? "audio/wav" : "image/png" });

it("returns to the protected photo screen after login and handles the mobile menu", async () => {
  let auth = false;
  const request = mockHttp((url) => {
    if (url.pathname === "/api/health") return json({ detail: "Worker недоступний" }, 503);
    if (url.pathname === "/api/auth/me" && !auth) return json({ detail: "Потрібен вхід" }, 401);
    if (url.pathname === "/api/auth/login") {
      auth = true;
      return json({ user: profile });
    }
  });
  const { user } = mount("/photo");
  expect(screen.getByText("Перевіряємо обліковий запис…")).toBeVisible();
  await screen.findByRole("heading", { name: "Вітаємо у Bird Lab" });
  await user.type(screen.getByLabelText("Електронна пошта"), profile.email);
  await user.type(screen.getByLabelText("Пароль"), "StrongPassword123!");
  await user.click(screen.getByRole("button", { name: "Увійти" }));
  expect(await screen.findByRole("heading", { name: "Птах у фокусі" })).toBeVisible();
  expect(await screen.findAllByText("Частина сервісів недоступна")).toHaveLength(2);
  await user.click(screen.getByRole("button", { name: "Меню" }));
  expect(screen.getByRole("button", { name: "Закрити меню" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Закрити меню" }));
  expect(screen.queryByRole("button", { name: "Закрити меню" })).toBeNull();
  expect(request.mock.calls.find(([url]) => String(url).endsWith("/auth/login"))?.[1]).toMatchObject({
    method: "POST", body: JSON.stringify({ email: profile.email, password: "StrongPassword123!" }),
  });
});

it("gates a paid route while the account and feature data load", async () => {
  mockHttp((url) => url.pathname === "/api/subscriptions/me" ? json({ ...access, plan: "FREE", features: ["image"] }) : undefined);
  mount("/audio");
  expect(await screen.findByRole("heading", { name: "Розширте можливості лабораторії" })).toBeVisible();
  expect(screen.getByText(/поточному тарифі FREE/)).toBeVisible();
  expect(screen.getByRole("link", { name: "Переглянути тарифи" })).toHaveAttribute("href", "/plans");
});

it("shows recent photo and audio, then filters, pages and opens history", async () => {
  const request = mockHttp((url) => {
    if (url.pathname !== "/api/analyses" || !url.searchParams.has("page")) return;
    return json(page(url.searchParams.get("analysisType") === "audio" ? [audio] : [photo, audio], 34));
  });
  const { user } = mount("/");
  expect(await screen.findByAltText("Останнє фото у вашій лабораторії")).toHaveAttribute("src", api.file(photo.fileUrl));
  expect(await screen.findByText("Аудіоспостереження")).toBeVisible();
  await user.click(screen.getByRole("link", { name: "Уся історія" }));
  expect(await screen.findByText("34 записів · сторінка 1 з 3")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Далі" }));
  expect(await screen.findByText("34 записів · сторінка 2 з 3")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Назад" }));
  await user.selectOptions(screen.getByLabelText("Тип аналізу"), "audio");
  await user.selectOptions(screen.getByLabelText("Сортування"), "confidence_desc");
  fireEvent.change(screen.getByLabelText("Пошук аналізів"), { target: { value: "song" } });
  await waitFor(() => expect(request.mock.calls.some(([input]) => {
    const url = new URL(String(input));
    return url.searchParams.get("search") === "song" && url.searchParams.get("page") === "1"
      && url.searchParams.get("sort") === "confidence_desc" && url.searchParams.get("analysisType") === "audio";
  })).toBe(true));
  await user.click(await screen.findByRole("link", { name: "Деталі аналізу 8" }));
  expect(await screen.findByText(/Висновок моделі збережено без/)).toBeVisible();
  expect(document.querySelector("audio")).toHaveAttribute("src", api.file(audio.fileUrl));
});

it("requires confirmation, surfaces delete failure and refreshes history after successful deletion", async () => {
  let deletes = 0;
  mockHttp((url, init) => {
    if (url.pathname === "/api/analyses/7" && init?.method === "DELETE") {
      return ++deletes === 1 ? json({ detail: "Файл зайнятий" }, 503) : new Response(null, { status: 204 });
    }
    if (url.pathname === "/api/analyses" && deletes === 2) return json(page([]));
  });
  const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
  const { user } = mount("/history/7");
  expect(await screen.findByText(/Еталон: Cardinal/)).toBeVisible();
  expect(screen.getByRole("link", { name: /Джерело еталонної/ })).toHaveAttribute("href", photo.evaluation!.labelSource);
  const button = screen.getByRole("button", { name: "Видалити" });
  await user.click(button);
  expect(deletes).toBe(0);
  await user.click(button);
  expect(await screen.findByRole("alert")).toHaveTextContent("Файл зайнятий");
  await user.click(button);
  expect(await screen.findByRole("heading", { name: "Моя історія" })).toBeVisible();
  expect(await screen.findByText("Тут починається ваше дослідження")).toBeVisible();
  expect(confirm).toHaveBeenCalledTimes(3);
});

it("submits multipart once, recovers a polling failure and restores the result after remount", async () => {
  let attempts = 0;
  const request = mockHttp((url, init) => {
    if (url.pathname === "/api/analysis/image") {
      const body = init?.body as FormData;
      expect((body.get("file") as File).name).toBe("bird.png");
      expect(init?.method).toBe("POST");
      return json(task, 202);
    }
    if (url.pathname === task.statusUrl) {
      if (++attempts === 1) return json(status("Queued", { stalled: true }));
      if (attempts === 2) return json({ detail: "Спробуйте пізніше" }, 503);
      return json(status("Completed", { result: photo }));
    }
  });
  const first = mount("/photo");
  const input = await screen.findByLabelText("Обрати файли");
  await first.user.upload(input, file());
  expect(await screen.findByAltText("Попередній перегляд")).toHaveAttribute("src", "blob:bird-preview");
  await first.user.click(screen.getByRole("button", { name: "Розпочати аналіз" }));
  expect(await screen.findByText(/Тривале очікування/)).toBeVisible();
  expect(await screen.findByText(/Спробуйте пізніше/)).toBeVisible();
  expect(await screen.findByText("Mallard", {}, { timeout: 3000 })).toBeVisible();
  expect(screen.getByText("Blue Jay")).toBeVisible();
  expect(screen.getByText("Cardinal")).toBeVisible();
  first.unmount();
  expect(revokeObjectURL).toHaveBeenCalledWith("blob:bird-preview");
  mount("/photo");
  expect(await screen.findByText("Mallard")).toBeVisible();
  expect(request.mock.calls.filter(([url]) => String(url).endsWith("/api/analysis/image"))).toHaveLength(1);
});

it("validates batch size and empty files, then shows mixed outcomes", async () => {
  mockHttp((url, init) => {
    if (url.pathname === "/api/analysis/batch") {
      expect((init?.body as FormData).getAll("files")).toHaveLength(2);
      return json([{ fileName: "bird.png", task }, { fileName: "other.png", error: "Вичерпано ліміт" }], 202);
    }
    if (url.pathname === task.statusUrl) return json(status("Completed", { result: photo }));
  });
  const { user } = mount("/photo");
  const input = await screen.findByLabelText("Обрати файли");
  await waitFor(() => expect(screen.getByText("Сервіси доступні")).toBeVisible());
  await waitFor(() => expect(screen.getByRole("link", { name: "Мій обліковий запис" })).toBeVisible());
  await user.upload(input, new File([], "empty.png", { type: "image/png" }));
  expect(screen.getByRole("alert")).toHaveTextContent("Файл порожній");
  await user.upload(input, Array.from({ length: 16 }, (_, i) => file(i + ".png")));
  expect(screen.getByRole("alert")).toHaveTextContent("не більше 15");
  await user.upload(input, [file(), file("other.png")]);
  await user.click(screen.getByRole("button", { name: "Проаналізувати 2 фото" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("other.png: Вичерпано ліміт");
  expect(await screen.findByRole("link", { name: /Cardinal · Відкрити результат/ })).toHaveAttribute("href", "/history/7");
});

it("keeps audio retry local to the selected file and shows the worker's terminal error", async () => {
  let uploads = 0;
  mockHttp((url, init) => {
    if (url.pathname === "/api/analysis/audio") {
      expect(((init?.body as FormData).get("file") as File).name).toBe("song.wav");
      return ++uploads === 1 ? json({ detail: "Мережа недоступна" }, 503) : json(task, 202);
    }
    if (url.pathname === task.statusUrl) return json(status("Failed", { errorMessage: "Формат аудіо пошкоджений" }));
  });
  const { user } = mount("/audio");
  const input = await screen.findByLabelText("Обрати файли");
  fireEvent.dragOver(input.closest("label")!);
  fireEvent.drop(input.closest("label")!, { dataTransfer: { files: [file("song.wav"), file("ignored.wav")] } });
  expect(document.querySelector("audio")).toHaveAttribute("src", "blob:bird-preview");
  await user.click(screen.getByRole("button", { name: "Розпочати аналіз" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Мережа недоступна");
  await user.click(screen.getByRole("button", { name: "Розпочати аналіз" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Формат аудіо пошкоджений");
  expect(uploads).toBe(2);
});

it("requests comparisons only for distinct ids and shows model uncertainty", async () => {
  const jay = { ...bird, id: 12, ukrainianName: "", commonName: "Blue Jay", scientificName: "Cyanocitta cristata", family: "" };
  const request = mockHttp((url) => {
    if (url.pathname === "/api/birds") return json([bird, jay]);
    if (url.pathname === "/api/birds/compare") return json({ first: bird, second: jay });
    if (url.pathname === "/api/analyses/compare") return json({ first: photo, second: audio, conclusion: "Потрібна незалежна перевірка" });
  });
  const { user } = mount("/compare");
  await user.selectOptions(await screen.findByLabelText("Перший вид"), "11");
  await user.selectOptions(screen.getByLabelText("Другий вид"), "11");
  expect(screen.getByRole("alert")).toHaveTextContent("два різні види");
  expect(request.mock.calls.filter(([url]) => String(url).includes("/birds/compare"))).toHaveLength(0);
  await user.selectOptions(screen.getByLabelText("Другий вид"), "12");
  expect(await screen.findByRole("cell", { name: "Cardinalidae" })).toBeVisible();
  await user.selectOptions(screen.getByLabelText("Перший результат"), "7");
  await user.selectOptions(screen.getByLabelText("Другий результат"), "7");
  expect(screen.getByRole("alert")).toHaveTextContent("два різні аналізи");
  await user.selectOptions(screen.getByLabelText("Другий результат"), "8");
  expect(await screen.findByText("Потрібна незалежна перевірка")).toBeVisible();
  expect(screen.getByRole("cell", { name: "Не зіставлено" })).toBeVisible();
});

it("shows evaluation totals only when the subscription includes advanced statistics", async () => {
  const request = mockHttp();
  const view = mount("/statistics");
  expect(await screen.findByRole("cell", { name: "1 / 2" }, { timeout: 3000 })).toBeVisible();
  expect(screen.getByRole("cell", { name: "2 / 2" })).toBeVisible();
  expect(screen.getByRole("heading", { name: "Розподіл confidence" })).toBeVisible();
  view.unmount();
  const limited = mockHttp((url) => url.pathname === "/api/subscriptions/me"
    ? json({ ...access, features: ["statistics"] }) : undefined);
  mount("/statistics");
  expect(await screen.findByText(/Ще немає аналізів із перевіреними/)).toBeVisible();
  expect(request.mock.calls.some(([url]) => String(url).includes("/statistics/evaluations"))).toBe(true);
  expect(limited.mock.calls.some(([url]) => String(url).includes("/statistics/evaluations"))).toBe(false);
});

it("opens a species card from gallery and recovers an unknown route", async () => {
  mockHttp();
  const { user, unmount } = mount("/gallery");
  const card = await screen.findByRole("heading", { name: bird.ukrainianName });
  await user.click(card);
  const panel = await screen.findByRole("heading", { name: "Аналіз № 7" });
  expect(panel).toBeVisible();
  await user.click(await screen.findByRole("link", { name: "Картка виду" }));
  expect(await screen.findByRole("heading", { level: 1, name: bird.ukrainianName })).toBeVisible();
  expect(screen.getByRole("link", { name: "Джерело 1" })).toHaveAttribute("href", bird.sources[0]);
  unmount();
  const missing = mount("/unknown");
  expect(await screen.findByRole("heading", { name: "Сторінку не знайдено" })).toBeVisible();
  await missing.user.click(screen.getByRole("link", { name: "Повернутися до лабораторії" }));
  expect(await screen.findByRole("heading", { name: /Відкрийте світ/ })).toBeVisible();
});

it("renders payment states and clears only analysis tasks on logout", async () => {
  const key = "bird-tasks:web-qa:fixture:image";
  localStorage.setItem(key, "[]");
  localStorage.setItem("other-app", "preserved");
  let loggedOut = false;
  mockHttp((url) => {
    if (url.pathname === "/api/subscriptions/payments") return json([
      { id: "pay-1", subscriptionPlanId: 1, amount: "5000000", tokenDecimals: 6, status: "Confirmed",
        blockchainNetwork: "Test", walletAddress: "0x1111111111111111111111111111111111111111", transactionHash: "0x1",
        createdAt: access.startDate, explorerUrl: "https://example.test/tx/1" },
      { id: "pay-2", subscriptionPlanId: 2, amount: "12500000", tokenDecimals: 6, status: "Reorged",
        blockchainNetwork: "Test", walletAddress: "0x2222222222222222222222222222222222222222", transactionHash: "0x2",
        createdAt: access.startDate, error: "Подія поза канонічним блоком" },
    ]);
    if (url.pathname === "/api/subscriptions/history") return json([
      { id: "period", planId: 1, status: "Expired", startDate: access.startDate, endDate: access.endDate,
        analysesUsed: 20, analysisLimit: 100 },
    ]);
    if (url.pathname === "/api/auth/logout") {
      loggedOut = true;
      return new Response(null, { status: 204 });
    }
    if (url.pathname === "/api/auth/me" && loggedOut) return json({ detail: "Session ended" }, 401);
  });
  const { user } = mount("/payments");
  expect(await screen.findByText("PRO · 5 тестових USDC")).toBeVisible();
  expect(screen.getByText("PREMIUM · 12.5 тестових USDC")).toBeVisible();
  expect(screen.getByText("Подія поза канонічним блоком")).toBeVisible();
  expect(screen.getByRole("link", { name: /Переглянути транзакцію/ })).toHaveAttribute("href", "https://example.test/tx/1");
  await user.click(within(screen.getByRole("navigation")).getByRole("link", { name: "Моя підписка" }));
  expect(await screen.findByText("PRO · Строк минув")).toBeVisible();
  await user.click(await screen.findByRole("button", { name: /Вийти/ }));
  expect(await screen.findByRole("heading", { name: "Вітаємо у Bird Lab" })).toBeVisible();
  expect(localStorage.getItem(key)).toBeNull();
  expect(localStorage.getItem("other-app")).toBe("preserved");
});

it("keeps the selected form available after a registration failure", async () => {
  mockHttp((url) => {
    if (url.pathname === "/api/auth/me") return json({ detail: "Потрібен вхід" }, 401);
    if (url.pathname === "/api/auth/register") return json({ detail: "Адресу вже використано" }, 400);
  });
  const { user } = mount("/login");
  await user.click(screen.getByRole("button", { name: "Створити новий обліковий запис" }));
  await user.type(screen.getByLabelText("Електронна пошта"), profile.email);
  await user.type(screen.getByLabelText("Пароль"), "TestPassword123!");
  await user.click(screen.getByRole("button", { name: "Зареєструватися" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Адресу вже використано");
  await user.click(screen.getByRole("button", { name: "У мене вже є обліковий запис" }));
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.getByLabelText("Електронна пошта")).toHaveValue(profile.email);
});

it("shows the home fallback and retries an unavailable history request", async () => {
  let failed = true;
  mockHttp((url) => {
    if (url.pathname === "/api/analyses") return failed ? json({ detail: "Історія тимчасово недоступна" }, 503) : json(page([]));
  });
  const { user } = mount("/");
  expect(await screen.findByText("Готові до першого спостереження")).toBeVisible();
  expect(await screen.findByRole("alert")).toHaveTextContent("Історія тимчасово недоступна");
  failed = false;
  await user.click(screen.getByRole("button", { name: "Спробувати ще раз" }));
  expect(await screen.findByText("Тут починається ваше дослідження")).toBeVisible();
});


it.each([
  ["image", "/photo", "nonbird.png", "На зображенні не вдалося виявити птаха. Спробуйте завантажити іншу фотографію."],
  ["audio", "/audio", "speech.wav", "На аудіозаписі не вдалося виявити звуків птахів. Завантажте інший запис."],
])("shows the server content rejection for %s without a species result", async (kind, route, name, message) => {
  const request = mockHttp((url) => {
    if (url.pathname === `/api/analysis/${kind}`) return json(task, 202);
    if (url.pathname === task.statusUrl) return json(status("Failed", { errorMessage: message }));
  });
  const { user } = mount(route);
  const input = await screen.findByLabelText("Обрати файли");
  fireEvent.drop(input.closest("label")!, { dataTransfer: { files: [file(name)] } });
  await user.click(screen.getByRole("button", { name: "Розпочати аналіз" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(message);
  expect(screen.getByTestId("analysis-task")).toHaveAttribute("data-status", "Failed");
  expect(screen.queryByText("Cardinal")).toBeNull();
  expect(screen.queryByRole("link", { name: /Відкрити результат/ })).toBeNull();
  const polls = request.mock.calls.filter(([url]) => String(url).endsWith(task.statusUrl));
  expect(polls).toHaveLength(1);
});
