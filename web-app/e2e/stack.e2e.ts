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
