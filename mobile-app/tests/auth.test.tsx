import { cleanup, fireEvent, render, screen } from "@testing-library/react-native";
import * as SecureStore from "expo-secure-store";
import App from "../App";
import { json, mockHttp, press, profile, resetNative, token } from "./fixtures";

beforeEach(resetNative);
afterEach(() => {
  cleanup();
  jest.restoreAllMocks();
});

it("removes an expired stored token and uses a newly issued bearer for private data", async () => {
  let authenticated = false;
  const fetch = mockHttp((url, init) => {
    if (url.pathname === "/api/auth/me" && !authenticated) return json({ detail: "Expired" }, 401);
    if (url.pathname === "/api/auth/login") {
      authenticated = true;
      expect(JSON.parse(init!.body as string)).toEqual({ email: profile.email, password: "Example123!Pass", useBearerToken: true });
      return json({ user: profile, accessToken: token });
    }
  });
  render(<App />);
  await screen.findByText("Увійти до лабораторії");
  expect(screen.getByRole("button", { name: "Увійти" })).toBeDisabled();
  await screen.findByRole("button", { name: "Увійти" });
  fireEvent.changeText(screen.getByLabelText("Електронна пошта"), " " + profile.email);
  fireEvent.changeText(screen.getByLabelText("Пароль"), "Example123!Pass");
  press("Увійти");
  expect(await screen.findByText("Світ птахів поруч.")).toBeOnTheScreen();
  expect(SecureStore.deleteItemAsync).toHaveBeenCalledWith("birdlab.auth.session");
  expect(SecureStore.setItemAsync).toHaveBeenCalledWith("birdlab.auth.session", token, {
    keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
  });
  expect(fetch.mock.calls.find(([url]) => String(url).includes("/analyses"))?.[1]?.headers).toEqual({ Authorization: "Bearer " + token });
});

it("retries a failed session restore and keeps the account after connectivity returns", async () => {
  let online = false;
  mockHttp((url) => url.pathname === "/api/auth/me" && !online ? json({ detail: "Немає зв’язку" }, 503) : undefined);
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Немає зв’язку");
  online = true;
  press("Перевірити сесію знову");
  expect(await screen.findByText("Світ птахів поруч.")).toBeOnTheScreen();
});

it("rejects registration without a bearer token and permits switching back to login", async () => {
  jest.mocked(SecureStore.getItemAsync).mockResolvedValue(null);
  mockHttp((url) => url.pathname === "/api/auth/register" ? json({ user: profile, accessToken: null }) : undefined);
  render(<App />);
  press("Створити обліковий запис");
  fireEvent.changeText(screen.getByLabelText("Електронна пошта"), profile.email);
  fireEvent.changeText(screen.getByLabelText("Пароль"), "Example123!Pass");
  press("Зареєструватися");
  expect(await screen.findByRole("alert")).toHaveTextContent("Сервер не повернув сесію.");
  expect(SecureStore.setItemAsync).not.toHaveBeenCalled();
  press("У мене вже є обліковий запис");
  expect(screen.getByRole("button", { name: "Увійти" })).toBeEnabled();
});
