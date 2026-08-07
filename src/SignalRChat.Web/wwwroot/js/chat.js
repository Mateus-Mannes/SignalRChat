"use strict";

function ensureAffinityCookie() {
    var cookieName = "signalr_affinity";
    var hasAffinityCookie = document.cookie
        .split(";")
        .some(function (cookie) {
            return cookie.trim().startsWith(`${cookieName}=`);
        });

    if (hasAffinityCookie) {
        return;
    }

    var affinityId = window.crypto && window.crypto.randomUUID
        ? window.crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(16).slice(2)}`;

    document.cookie = `${cookieName}=${encodeURIComponent(affinityId)}; Path=/; SameSite=Lax`;
}

ensureAffinityCookie();

var hubUrl = window.signalRChat && window.signalRChat.hubUrl
    ? window.signalRChat.hubUrl
    : "/chatHub";
var apiBaseUrl = window.signalRChat && window.signalRChat.apiBaseUrl
    ? window.signalRChat.apiBaseUrl
    : "";

var connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, { withCredentials: true })
    .build();

var authStatus = document.getElementById("authStatus");
var emailInput = document.getElementById("emailInput");
var passwordInput = document.getElementById("passwordInput");
var registerButton = document.getElementById("registerButton");
var loginButton = document.getElementById("loginButton");
var logoutButton = document.getElementById("logoutButton");
var sendButton = document.getElementById("sendButton");
var messageInput = document.getElementById("messageInput");
var messagesList = document.getElementById("messagesList");

sendButton.disabled = true;
logoutButton.disabled = true;

connection.on("ReceiveMessage", function (user, message) {
    var li = document.createElement("li");
    messagesList.appendChild(li);
    // We can assign user-supplied strings to an element's textContent because it
    // is not interpreted as markup. If you're assigning in any other way, you 
    // should be aware of possible script injection concerns.
    li.textContent = `${user} says ${message}`;
});

async function request(path, options) {
    var response = await fetch(`${apiBaseUrl}${path}`, {
        credentials: "include",
        headers: {
            "Content-Type": "application/json"
        },
        ...options
    });

    if (!response.ok) {
        var details = await response.text();
        throw new Error(details || response.statusText);
    }

    return response;
}

async function startChat() {
    if (connection.state !== signalR.HubConnectionState.Disconnected) {
        return;
    }

    await connection.start();
    sendButton.disabled = false;
}

async function stopChat() {
    if (connection.state !== signalR.HubConnectionState.Disconnected) {
        await connection.stop();
    }

    sendButton.disabled = true;
}

async function refreshUser() {
    try {
        var response = await request("/account/me", { method: "GET" });
        var user = await response.json();
        authStatus.textContent = `Logged in as ${user.email}`;
        logoutButton.disabled = false;
        loginButton.disabled = true;
        registerButton.disabled = true;
        await startChat();
    } catch {
        authStatus.textContent = "Not logged in";
        logoutButton.disabled = true;
        loginButton.disabled = false;
        registerButton.disabled = false;
        await stopChat();
    }
}

async function register() {
    await request("/register", {
        method: "POST",
        body: JSON.stringify({
            email: emailInput.value,
            password: passwordInput.value
        })
    });

    await login();
}

async function login() {
    await request("/login?useCookies=true", {
        method: "POST",
        body: JSON.stringify({
            email: emailInput.value,
            password: passwordInput.value
        })
    });

    passwordInput.value = "";
    await refreshUser();
}

async function logout() {
    await request("/logout", { method: "POST" });
    await refreshUser();
}

function reportError(error) {
    authStatus.textContent = error.message || error.toString();
}

registerButton.addEventListener("click", function () {
    register().catch(reportError);
});

loginButton.addEventListener("click", function () {
    login().catch(reportError);
});

logoutButton.addEventListener("click", function () {
    logout().catch(reportError);
});

sendButton.addEventListener("click", function (event) {
    var message = messageInput.value;
    connection.invoke("SendMessage", message).catch(function (err) {
        reportError(err);
    });
    event.preventDefault();
});

refreshUser();
