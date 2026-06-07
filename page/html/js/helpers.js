window.helpers = {
  setAttribute: (element, name, value) => {
    if (element) {
      element.setAttribute(name, value);
    }
  },
  setVisibility: (element, visibility) => {
    if (visibility) {
      element.style.removeProperty("display");
    } else {
      element.style.display = "none";
    }
  }
};