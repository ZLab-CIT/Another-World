const $=id => document.getElementById(id);
let token = localStorage.getItem('awVisitorToken')||'';
let state = null;

async function api(url, options={}) {
    options.headers = {...(options.headers || {}),'Content-Type':'application/json'};
    if (token) options.headers['X-Visitor-Token'] = token;
    const response = await fetch(url, options);
    if (!response.ok) throw new Error(await response.text());
    return response.status === 202 ? null : response.json()
}

async function register() {
    const visitor = await api('/api/visitors/register',
    {
        method:'POST',
        body:JSON.stringify({alias:$('alias').value, publicAliasConsent:$('publicAlias').checked})
    });
    token = visitor.token;
    localStorage.setItem('awVisitorToken', token);
    await refresh();
    await loadVisitors()
}

async function vote(decisionId, optionId) {
    document.querySelectorAll('.option').forEach(x => x.disabled=true);
    await api(`/api/decisions/${decisionId}/votes`,
    {
        method:'POST',
        body:JSON.stringify({optionId})
    });
    await refresh()
}

function render() {
    const visitor = state?.visitor;
    $('profile').classList.toggle('hidden',!!visitor);
    $('forget').classList.toggle('hidden',!visitor);
    $('identity').textContent = visitor?`Welcome back, ${visitor.displayName}. Your choices have shaped ${visitor.storyContributions} office stories.`:'Choose an optional identity to participate.';
    const decision = state?.activeDecision;
    $('decision').classList.toggle('hidden', !decision);
    $('decisionIdle').classList.toggle('hidden', !!decision);
    if (decision) {
        $('author').textContent = `${decision.authorDisplayName} wants your opinion`;
        $('question').textContent = decision.question;
        $('options').innerHTML='';
        decision.options.forEach(option => {
            const button = document.createElement('button');
            button.className = 'option';
            button.disabled = !!state.visitorVoteOptionId;
            button.innerHTML = `<span>${escapeHtml(option.label)}${state.visitorVoteOptionId===option.optionId?' ✓':''}</span><b>${option.votes}</b>`;
            button.onclick = () => vote(decision.decisionId, option.optionId);
            $('options').appendChild(button)});
            const seconds = Math.max(0,Math.ceil((decision.closesAtUnixMilliseconds - Date.now()) / 1000));
            $('timer').textContent = state.visitorVoteOptionId?`Vote recorded. Result in about ${seconds} seconds.`:`Voting closes in about ${seconds} seconds. The majority changes what happens next.`
    }
    const claimable = state?.claimableReward;
    $('claimOffer').classList.toggle('hidden', !claimable);
    if (claimable) {
        $('claimName').textContent = claimable.displayName;
        $('claimDescription').textContent = claimable.description || 'A character prepared this for someone nearby.';
        const claimSeconds = Math.max(0, Math.ceil((claimable.expiresAtUnixMilliseconds-Date.now())/1000));
        $('claimTimer').textContent = `Claim before ${formatDate(claimable.expiresAtUnixMilliseconds)}. About ${Math.ceil(claimSeconds/60)} minute${claimSeconds>60?'s':''} remaining.`;
        $('claimReward').disabled = !visitor;
        $('claimReward').textContent = visitor ? 'Claim this coupon' : 'Register above to claim';
    }
    const paper = state?.latestNewspaper;
    $('newspaper').classList.toggle('hidden', !paper);
    if (paper) {
        $('paperDate').textContent = paper.date;
        $('headline').textContent = paper.headline;
        $('summary').textContent = paper.summary;
        $('stories').innerHTML = paper.stories.map(story => `<article>${escapeHtml(story)}</article>`).join('');
        $('quote').textContent = paper.quote;
        $('decisionResult').textContent = paper.decisionResult;
        $('visitorThanks').textContent = paper.visitorAcknowledgement;
        $('teaser').textContent = paper.tomorrowTeaser
    }
    const reward = state?.rewards?.[0];
    $('reward').classList.toggle('hidden', !reward);
    if (reward) {
        $('rewardName').textContent = reward.displayName;
        $('rewardCode').textContent = reward.code;
        const expired = reward.expiresAtUnixMilliseconds <= Date.now();
        $('rewardExpiration').textContent = `Valid until ${formatDate(reward.expiresAtUnixMilliseconds)}.`;
        $('useReward').disabled = reward.used || expired;
        $('useReward').textContent = reward.used ? 'Coupon used' : expired ? 'Coupon expired' : 'Use coupon';
        $('rewardStatus').textContent = reward.used
            ? `Used ${formatDate(reward.usedAtUnixMilliseconds)}.`
            : expired ? 'This prototype coupon has expired.' : 'Ready to use.'
    }
}

async function refresh() {
    try {
        state = await api('/api/state');
        if (state?.visitor?.token && state.visitor.token !== token) {
            token = state.visitor.token;
            localStorage.setItem('awVisitorToken', token)
        }
        render();
        renderRewardQr()
    } catch
    {
        $('identity').textContent = 'The virtual office is temporarily unreachable.'
    }
}

async function recordQrScan() {
    let scanId = sessionStorage.getItem('awQrScanId');
    if (!scanId) {
        scanId = globalThis.crypto?.randomUUID?.()
            || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        sessionStorage.setItem('awQrScanId', scanId)
    }
    try {
        await api('/api/visits/scan', {
            method:'POST',
            body:JSON.stringify({scanId})
        })
    } catch {}
}

function renderRewardQr() {
    const reward = state?.rewards?.[0];
    if (!reward) return;
    const target = `${location.origin}/rewards/${encodeURIComponent(reward.code)}`;
    $('rewardQr').src=`/api/qr?url=${encodeURIComponent(target)}`
}

async function loadVisitors() {
    try {
        const values = await api('/api/visitors/public');
        const recipients = values.filter(x => x.visitorId !== state?.visitor?.visitorId);
        const ready = !!state?.visitor && recipients.length > 0;
        $('recipient').innerHTML = ready
            ? recipients.map(x => `<option value="${x.visitorId}">${escapeHtml(x.displayName)}</option>`).join('')
            : `<option value="">${state?.visitor ? 'No other registered colleague yet' : 'Register yourself first'}</option>`;
        $('recipient').disabled = !ready;
        $('appreciate').disabled = !ready;
        $('appreciationStatus').textContent = !state?.visitor ? 'Register your visitor identity first.' : recipients.length === 0 ? 'No other public visitor aliases are available yet. Open this page in another browser or phone and register a second alias.' : `${recipients.length} colleague alias${recipients.length===1?' is':'es are'} available.`
    } catch {
        $('appreciationStatus').textContent = 'Could not load colleague aliases.'
    }
}

async function loadArchive() {
    try {
        const papers = await api('/api/newspapers');
        $('archive').innerHTML = papers.map(p => `<article><b>${escapeHtml(p.date)}: ${escapeHtml(p.headline)}</b><br>${escapeHtml(p.summary)}</article>`).join('')||'No past editions yet.'
    } catch {}
}

async function appreciate () {
    const recipient = $('recipient').value;
    if (!recipient) {
        $('appreciationStatus').textContent = 'A second opted-in colleague is required.';
        return
    }
    try {
        await api('/api/appreciation', {
            method:'POST',
            body:JSON.stringify({
                recipientVisitorId:recipient,
                category:$('category').value,
                courierAgentId:$('courier').value,
                publicConsent:$('publicNote').checked
            })
        });
        $('appreciationStatus').textContent = 'The character accepted and delivered your note.'
    } catch {
        $('appreciationStatus').textContent='The note could not be delivered. Refresh and try again.'
    }
}

async function claimReward() {
    const reward = state?.claimableReward;
    if (!reward || !state?.visitor) return;
    $('claimReward').disabled = true;
    try {
        await api(`/api/rewards/${encodeURIComponent(reward.code)}/claim`, {method:'POST'});
        $('claimStatus').textContent = 'Claimed. This coupon now belongs to your browser identity.';
        await refresh()
    } catch {
        $('claimStatus').textContent = 'Someone else claimed it, or the five-minute window expired.';
        await refresh()
    }
}

async function useReward() {
    const reward = state?.rewards?.[0];
    if (!reward || reward.used || reward.expiresAtUnixMilliseconds <= Date.now()) return;
    $('useReward').disabled = true;
    try {
        await api(`/api/rewards/${encodeURIComponent(reward.code)}/use`, {method:'POST'});
        $('rewardStatus').textContent = 'Coupon used successfully.';
    } catch {
        $('rewardStatus').textContent = 'The coupon was already used or has expired.'
    }
    await refresh()
}

function formatDate(value) {
    return new Date(value).toLocaleString([], {
        year:'numeric', month:'short', day:'numeric', hour:'2-digit', minute:'2-digit'
    })
}

function escapeHtml(value) {
    const element = document.createElement('span');
    element.textContent = value || '';
    return element.innerHTML
}

$('register').onclick = register;
$('forget').onclick = () => {
    localStorage.removeItem('awVisitorToken');
    token = '';
    state = null;
    refresh().then(loadVisitors)
};
$('appreciate').onclick = appreciate;
$('claimReward').onclick = claimReward;
$('useReward').onclick = useReward;
recordQrScan();
refresh().then(loadVisitors);
loadArchive();
setInterval(() => {
    refresh().then(loadVisitors)},2500);
    const source = new EventSource('/api/events');
    source.addEventListener('hub', () => { refresh().then(loadVisitors); loadArchive() }
);
