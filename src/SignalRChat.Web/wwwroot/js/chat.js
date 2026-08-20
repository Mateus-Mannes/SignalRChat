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
var conversationPanel = document.getElementById("conversationPanel");
var conversationNameInput = document.getElementById("conversationNameInput");
var createConversationButton = document.getElementById("createConversationButton");
var refreshConversationsButton = document.getElementById("refreshConversationsButton");
var conversationStatus = document.getElementById("conversationStatus");
var conversationSelect = document.getElementById("conversationSelect");
var conversationDetails = document.getElementById("conversationDetails");
var selectedConversationName = document.getElementById("selectedConversationName");
var selectedConversationSummary = document.getElementById("selectedConversationSummary");
var ownerMembershipControls = document.getElementById("ownerMembershipControls");
var memberEmailInput = document.getElementById("memberEmailInput");
var addMemberButton = document.getElementById("addMemberButton");
var membersList = document.getElementById("membersList");
var leaveConversationButton = document.getElementById("leaveConversationButton");
var conversations = [];
var currentConversation = null;

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
        var details;

        try {
            details = await response.json();
        } catch {
            details = null;
        }

        var error = new Error(
            details && (details.detail || details.title)
                ? details.detail || details.title
                : response.statusText);
        error.code = details && details.code;
        throw error;
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
    var response;

    try {
        response = await request("/account/me", { method: "GET" });
    } catch {
        authStatus.textContent = "Not logged in";
        logoutButton.disabled = true;
        loginButton.disabled = false;
        registerButton.disabled = false;
        resetConversations();
        await stopChat();
        return;
    }

    var user = await response.json();
    authStatus.textContent = `Logged in as ${user.email}`;
    logoutButton.disabled = false;
    loginButton.disabled = true;
    registerButton.disabled = true;
    conversationPanel.hidden = false;
    await startChat();

    try {
        await refreshConversations();
    } catch (error) {
        reportConversationError(error);
    }
}

function resetConversations() {
    conversations = [];
    currentConversation = null;
    conversationPanel.hidden = true;
    conversationDetails.hidden = true;
    conversationStatus.textContent = "";
    conversationSelect.replaceChildren();
    membersList.replaceChildren();
}

function setConversationStatus(message) {
    conversationStatus.textContent = message;
}

function reportConversationError(error) {
    setConversationStatus(error.message || error.toString());
}

async function refreshConversations(preferredConversationId) {
    setConversationStatus("Loading...");
    var response = await request("/conversations?limit=100", { method: "GET" });
    var result = await response.json();
    conversations = result.items;

    conversationSelect.replaceChildren();

    if (conversations.length === 0) {
        var emptyOption = document.createElement("option");
        emptyOption.value = "";
        emptyOption.textContent = "No conversations yet";
        conversationSelect.appendChild(emptyOption);
        conversationSelect.disabled = true;
        conversationDetails.hidden = true;
        currentConversation = null;
        setConversationStatus("");
        return;
    }

    conversationSelect.disabled = false;
    conversations.forEach(function (conversation) {
        var option = document.createElement("option");
        option.value = conversation.id;
        option.textContent = `${conversation.name} (${conversation.activeMemberCount})`;
        conversationSelect.appendChild(option);
    });

    var selectedId = preferredConversationId
        || (currentConversation && currentConversation.id)
        || conversations[0].id;
    var selectedExists = conversations.some(function (conversation) {
        return conversation.id === selectedId;
    });
    conversationSelect.value = selectedExists ? selectedId : conversations[0].id;

    if (result.nextCursor) {
        setConversationStatus("Showing the newest 100 conversations.");
    } else {
        setConversationStatus("");
    }

    await loadSelectedConversation();
}

async function loadSelectedConversation() {
    var conversationId = conversationSelect.value;

    if (!conversationId) {
        return;
    }

    var detailResponse = await request(`/conversations/${conversationId}`, { method: "GET" });
    var membersResponse = await request(`/conversations/${conversationId}/members`, { method: "GET" });
    currentConversation = await detailResponse.json();
    var members = await membersResponse.json();

    selectedConversationName.textContent = currentConversation.name;
    selectedConversationSummary.textContent =
        ` — ${currentConversation.currentUserRole}, ${currentConversation.activeMemberCount}/10 members`;
    ownerMembershipControls.hidden = currentConversation.currentUserRole !== "owner";
    leaveConversationButton.hidden = currentConversation.currentUserRole === "owner";
    renderMembers(members);
    conversationDetails.hidden = false;
}

function renderMembers(members) {
    membersList.replaceChildren();

    members.forEach(function (member) {
        var item = document.createElement("li");
        var label = document.createElement("span");
        label.textContent = `${member.email} (${member.role})`;
        item.appendChild(label);

        if (currentConversation.currentUserRole === "owner" && member.role !== "owner") {
            var removeButton = document.createElement("button");
            removeButton.type = "button";
            removeButton.textContent = "Remove";
            removeButton.className = "ms-2";
            removeButton.dataset.userId = member.userId;
            removeButton.addEventListener("click", function () {
                removeMember(member.userId).catch(reportConversationError);
            });
            item.appendChild(removeButton);
        }

        membersList.appendChild(item);
    });
}

async function createConversation() {
    var response = await request("/conversations", {
        method: "POST",
        body: JSON.stringify({ name: conversationNameInput.value })
    });
    var conversation = await response.json();
    conversationNameInput.value = "";
    await refreshConversations(conversation.id);
    setConversationStatus("Conversation created.");
}

async function addMember() {
    await request(`/conversations/${currentConversation.id}/members`, {
        method: "POST",
        body: JSON.stringify({ email: memberEmailInput.value })
    });
    memberEmailInput.value = "";
    await refreshConversations(currentConversation.id);
    setConversationStatus("Member added.");
}

async function removeMember(userId) {
    var conversationId = currentConversation.id;
    await request(`/conversations/${conversationId}/members/${encodeURIComponent(userId)}`, {
        method: "DELETE"
    });
    await refreshConversations(conversationId);
    setConversationStatus("Member removed.");
}

async function leaveConversation() {
    await request(`/conversations/${currentConversation.id}/members/me`, {
        method: "DELETE"
    });
    await refreshConversations();
    setConversationStatus("You left the conversation.");
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

createConversationButton.addEventListener("click", function () {
    createConversation().catch(reportConversationError);
});

refreshConversationsButton.addEventListener("click", function () {
    refreshConversations().catch(reportConversationError);
});

conversationSelect.addEventListener("change", function () {
    loadSelectedConversation().catch(reportConversationError);
});

addMemberButton.addEventListener("click", function () {
    addMember().catch(reportConversationError);
});

leaveConversationButton.addEventListener("click", function () {
    leaveConversation().catch(reportConversationError);
});

sendButton.addEventListener("click", function (event) {
    var message = messageInput.value;
    connection.invoke("SendMessage", message).catch(function (err) {
        reportError(err);
    });
    event.preventDefault();
});

refreshUser();
