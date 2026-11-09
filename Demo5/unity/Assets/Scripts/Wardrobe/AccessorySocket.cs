// An Outfit's socket at one location on the body: an XR socket that only takes accessories for that location.
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Freestyle.Wardrobe
{
    public class AccessorySocket : XRSocketInteractor
    {
        public Outfit Outfit { get; internal set; }
        public BodyLocation location;

        Accessory ejected;
        float ejectedUntil;

        /// <summary>Don't take this one back for a moment: it was just taken off (its trigger contact is stale until
        /// the next physics step, by which time it's back where it started).</summary>
        public void Ejected(Accessory a)
        {
            ejected = a;
            ejectedUntil = Time.time + 0.3f;
        }

        bool Fits(IXRInteractable interactable) =>
            interactable.transform.TryGetComponent(out Accessory a) && a.location == location &&
            !(a == ejected && Time.time < ejectedUntil);

        public override bool CanHover(IXRHoverInteractable interactable) => base.CanHover(interactable) && Fits(interactable);

        public override bool CanSelect(IXRSelectInteractable interactable) => base.CanSelect(interactable) && Fits(interactable);
    }
}
