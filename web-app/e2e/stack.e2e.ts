import { test, expect } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { resolve } from "node:path";

test("real photo travels from React through RabbitMQ and Hugging Face to SQL history and can be deleted", async ({ page }) => {
  await page.goto("/photo");
  await page.getByRole("button", { name: "Створити новий обліковий запис" }).click();
  await page.getByLabel("Електронна пошта").fill("browser-qa-" + randomUUID() + "@example.test");
  await page.getByLabel("Пароль").fill("BirdQA!" + randomUUID());
  await page.getByRole("button", { name: "Зареєструватися" }).click();
  await expect(page.getByRole("heading", { name: "Птах у фокусі" })).toBeVisible();
  const photo = resolve("../sample_images/01_Cardinalis_cardinalis.jpg");
  await page.getByLabel("Обрати файли").setInputFiles(photo);
  await expect(page.getByAltText("Попередній перегляд")).toBeVisible();
  const accepted = page.waitForResponse(response =>
    new URL(response.url()).pathname === "/api/analysis/image" && response.request().method() === "POST");
  await page.getByRole("button", { name: "Розпочати аналіз" }).click();
  const response = await accepted;
  expect(response.status()).toBe(202);
  const job = await response.json();
  expect(job.analysisId).toMatch(/^[0-9a-f-]{36}$/);
  const task = page.getByTestId("analysis-task");
  await expect(task).toHaveAttribute("data-status", "Completed", { timeout: 180_000 });
  await expect(task.locator(".rank")).toHaveText(["01", "02", "03"]);
  const api = new URL(response.url()).origin;
  const saved = await page.request.get(api + "/api/analyses/" + job.historyId);
  expect(saved.ok()).toBe(true);
  const row = await saved.json();
  expect(row.analysisId).toBe(job.analysisId);
  expect(row.modelRevision).toMatch(/^[a-f0-9]{40}$/);
  expect(row.predictions).toHaveLength(3);
  await page.reload();
  await expect(task).toHaveAttribute("data-status", "Completed");
  await task.getByRole("link", { name: "Відкрити збережений аналіз" }).click();
  await expect(page.getByRole("heading", { name: "Аналіз № " + job.historyId })).toBeVisible();
  page.once("dialog", dialog => dialog.accept());
  await page.getByRole("button", { name: "Видалити" }).click();
  await expect(page.getByRole("heading", { name: "Моя історія" })).toBeVisible();
  await expect(page.getByText("Тут починається ваше дослідження")).toBeVisible();
  expect((await page.request.get(api + "/api/analyses/" + job.historyId)).status()).toBe(404);
  await test.info().attach("browser-evidence", {
    body: JSON.stringify({ analysisId: job.analysisId, historyId: job.historyId, modelId: row.modelName,
      modelRevision: row.modelRevision, predictions: row.predictions.length, historyAfterDeletion: 0 }),
    contentType: "application/json",
  });
});

test("a real non-bird photo fails in React without a species or a consumed quota unit", async ({ page }) => {
  await page.goto("/photo");
  await page.getByRole("button", { name: "Створити новий обліковий запис" }).click();
  await page.getByLabel("Електронна пошта").fill("browser-content-qa-" + randomUUID() + "@example.test");
  await page.getByLabel("Пароль").fill("BirdQA!" + randomUUID());
  await page.getByRole("button", { name: "Зареєструватися" }).click();
  await expect(page.getByRole("heading", { name: "Птах у фокусі" })).toBeVisible();
  const encoded = await page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = 256;
    const context = canvas.getContext("2d")!;
    context.fillStyle = "black";
    context.fillRect(0, 0, 256, 256);
    return canvas.toDataURL("image/png").split(",")[1];
  });
  await page.getByLabel("Обрати файли").setInputFiles({ name: "empty.png", mimeType: "image/png", buffer: Buffer.from(encoded, "base64") });
  const accepted = page.waitForResponse(response => new URL(response.url()).pathname === "/api/analysis/image" && response.request().method() === "POST");
  await page.getByRole("button", { name: "Розпочати аналіз" }).click();
  const response = await accepted;
  expect(response.status()).toBe(202);
  const api = new URL(response.url()).origin;
  const task = page.getByTestId("analysis-task");
  await expect(task).toHaveAttribute("data-status", "Failed", { timeout: 180_000 });
  await expect(task).toContainText("На зображенні не вдалося виявити птаха. Спробуйте завантажити іншу фотографію.");
  await expect(task.locator(".rank")).toHaveCount(0);
  const usage = await page.request.get(api + "/api/subscriptions/usage");
  expect((await usage.json()).remaining).toBe(5);
  const history = await page.request.get(api + "/api/analyses");
  expect((await history.json()).total).toBe(0);
  await page.reload();
  await expect(task).toHaveAttribute("data-status", "Failed");
  await expect(task.locator(".rank")).toHaveCount(0);
});
