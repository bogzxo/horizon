eventDispatcher.registerPacketHandler(0, updateCardInfo);

const LABEL_HEALTHY = "Healthy";
const LABEL_UNHEALTHY = "Unhealthy";

const COLOUR_UNHEALTHY = "red";
const COLOUR_WARNING = "yellow";
const COLOUR_HEALTHY = "green";

const RENDER_TARGET = 1 / 60.0;
const LOGIC_TARGET = 1 / 120.0;
const PHYSICS_TARGET = 1 / 40.0;

const SECOND_US_MULT = 1000000.0;
const SAFE_MARGIN = 3.0 / 8.0; // warning margin as a multiplier

function updateCard(title, card, targetRate, realRate) {
  // Updates the card title
  document.querySelector('#' + "card-" + card).childNodes[1].textContent =
    title + " " +  // Title
    Math.round(1.0 / realRate) + "hz (" + // FPS
    Math.round(realRate * SECOND_US_MULT) + "us/" + // Real FPS
    Math.round(targetRate * SECOND_US_MULT) + "us)"; // Target FPS

  // Updates the cards status
  const state = realRate < targetRate;
  const diff = targetRate - realRate;

  document.querySelector("#" + card + "-status-label").style.color = ((diff < (targetRate * SAFE_MARGIN) && state)) ? COLOUR_WARNING : (state ? COLOUR_HEALTHY : COLOUR_UNHEALTHY);
  document.querySelector("#" + card + "-status-label").textContent = state ? LABEL_HEALTHY : LABEL_UNHEALTHY;
}

const CARD_RENDER_TITLE = 'Render';
const CARD_LOGIC_TITLE = 'State';
const CARD_PHYSICS_TITLE = 'Physics';
function updateCardInfo(data) {
  updateCard(CARD_RENDER_TITLE, "render", RENDER_TARGET, data.RenderRate);
  updateCard(CARD_LOGIC_TITLE, "state", LOGIC_TARGET, data.LogicRate);
  updateCard(CARD_PHYSICS_TITLE, "physics", PHYSICS_TARGET, data.PhysicsRate);
}