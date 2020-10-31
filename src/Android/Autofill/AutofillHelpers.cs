using System.Collections.Generic;
using Android.Content;
using Android.Service.Autofill;
using Android.Widget;
using System.Linq;
using Android.App;
using System.Threading.Tasks;
using Bit.App.Resources;
using Bit.Core.Enums;
using Android.Views.Autofill;
using Bit.Core.Abstractions;
using Androidx.Autofill.Inline.V1;
using Android.Graphics.Drawables;
using Android.OS;
using Android.App.Slices;
using Android.Widget.Inline;
using Android.Views.InputMethods;
using Android.Util;
using Android.Text;
using Java.Lang;
using Android.Net;

namespace Bit.Droid.Autofill
{
    public static class AutofillHelpers
    {
        private static int _pendingIntentId = 0;

        // These browsers work natively with the Autofill Framework
        //
        // Be sure:
        //   - to keep these entries sorted alphabetically and
        //
        //   - ... to keep this list in sync with values in AccessibilityHelpers.SupportedBrowsers [Section A], too.
        public static HashSet<string> TrustedBrowsers = new HashSet<string>
        {
            "com.duckduckgo.mobile.android",
            "org.mozilla.focus",
            "org.mozilla.klar",
        };

        // These browsers work using the compatibility shim for the Autofill Framework
        //
        // Be sure:
        //   - to keep these entries sorted alphabetically,
        //   - to keep this list in sync with values in Resources/xml/autofillservice.xml, and
        //
        //   - ... to keep this list in sync with values in AccessibilityHelpers.SupportedBrowsers [Section A], too.
        public static HashSet<string> CompatBrowsers = new HashSet<string>
        {
            "com.amazon.cloud9",
            "com.android.browser",
            "com.android.chrome",
            "com.android.htmlviewer",
            "com.avast.android.secure.browser",
            "com.avg.android.secure.browser",
            "com.brave.browser",
            "com.brave.browser_beta",
            "com.brave.browser_default",
            "com.brave.browser_dev",
            "com.brave.browser_nightly",
            "com.chrome.beta",
            "com.chrome.canary",
            "com.chrome.dev",
            "com.ecosia.android",
            "com.google.android.apps.chrome",
            "com.google.android.apps.chrome_dev",
            "com.kiwibrowser.browser",
            "com.microsoft.emmx",
            "com.mmbox.browser",
            "com.mmbox.xbrowser",
            "com.naver.whale",
            "com.opera.browser",
            "com.opera.browser.beta",
            "com.opera.mini.native",
            "com.opera.mini.native.beta",
            "com.opera.touch",
            "com.qwant.liberty",
            "com.sec.android.app.sbrowser",
            "com.sec.android.app.sbrowser.beta",
            "com.stoutner.privacybrowser.free",
            "com.stoutner.privacybrowser.standard",
            "com.vivaldi.browser",
            "com.vivaldi.browser.snapshot",
            "com.vivaldi.browser.sopranos",
            "com.yandex.browser",
            "com.z28j.feel",
            "idm.internet.download.manager",
            "idm.internet.download.manager.adm.lite",
            "idm.internet.download.manager.plus",
            "io.github.forkmaintainers.iceraven",
            "mark.via",
            "mark.via.gp",
            "org.adblockplus.browser",
            "org.adblockplus.browser.beta",
            "org.bromite.bromite",
            "org.bromite.chromium",
            "org.chromium.chrome",
            "org.codeaurora.swe.browser",
            "org.gnu.icecat",
            "org.mozilla.fenix",
            "org.mozilla.fenix.nightly",
            "org.mozilla.fennec_aurora",
            "org.mozilla.fennec_fdroid",
            "org.mozilla.firefox",
            "org.mozilla.firefox_beta",
            "org.mozilla.reference.browser",
            "org.mozilla.rocket",
            "org.torproject.torbrowser",
            "org.torproject.torbrowser_alpha",
            "org.ungoogled.chromium",
            "org.ungoogled.chromium.extensions.stable",
            "org.ungoogled.chromium.stable",
        };

        // The URLs are blacklisted from autofilling
        public static HashSet<string> BlacklistedUris = new HashSet<string>
        {
            "androidapp://android",
            "androidapp://com.android.settings",
            "androidapp://com.x8bit.bitwarden",
            "androidapp://com.oneplus.applocker",
        };

        public static async Task<List<FilledItem>> GetFillItemsAsync(Parser parser, ICipherService cipherService)
        {
            if (parser.FieldCollection.FillableForLogin)
            {
                var ciphers = await cipherService.GetAllDecryptedByUrlAsync(parser.Uri);
                if (ciphers.Item1.Any() || ciphers.Item2.Any())
                {
                    var allCiphers = ciphers.Item1.ToList();
                    allCiphers.AddRange(ciphers.Item2.ToList());
                    return allCiphers.Select(c => new FilledItem(c)).ToList();
                }
            }
            else if (parser.FieldCollection.FillableForCard)
            {
                var ciphers = await cipherService.GetAllDecryptedAsync();
                return ciphers.Where(c => c.Type == CipherType.Card).Select(c => new FilledItem(c)).ToList();
            }
            return new List<FilledItem>();
        }

        public static FillResponse BuildFillResponse(Parser parser, List<FilledItem> items, bool locked, FillRequest request)
        {
            var responseBuilder = new FillResponse.Builder();
            var listSize = 0;
            if (items != null && items.Count > 0)
            {
                listSize = items.Count;
                if (Build.VERSION.SdkInt > BuildVersionCodes.Q)
                {
                    var inlineRequest = request.InlineSuggestionsRequest;
                    // Check that the keyboard in use actually supports inline autofill
                    if (inlineRequest != null && inlineRequest.InlinePresentationSpecs.Count > 0)
                        // Account for the app entrypoint and discard extra suggestions
                        listSize = Math.Min(inlineRequest.InlinePresentationSpecs.Count - 1, listSize);
                }

                for (var i = 0; i < listSize; i++)
                {
                    var dataset = BuildDataset(parser.ApplicationContext, parser.FieldCollection, items[i], request, i);
                    if (dataset != null)
                    {
                        responseBuilder.AddDataset(dataset);
                    }
                }
            }
            responseBuilder.AddDataset(BuildVaultDataset(parser.ApplicationContext, parser.FieldCollection,
                parser.Uri, locked, request, listSize));
            AddSaveInfo(parser, responseBuilder, parser.FieldCollection);
            responseBuilder.SetIgnoredIds(parser.FieldCollection.IgnoreAutofillIds.ToArray());
            return responseBuilder.Build();
        }

        public static Dataset BuildDataset(Context context, FieldCollection fields, FilledItem filledItem,
            FillRequest request = null, int index = 0)
        {
            var datasetBuilder = new Dataset.Builder(
                BuildListView(filledItem.Name, filledItem.Subtitle, filledItem.Icon, context));

            if (request != null && Build.VERSION.SdkInt > BuildVersionCodes.Q && request.InlineSuggestionsRequest != null)
            {
                var inlinePresentation = BuildInlinePresentation(
                    filledItem.Name, filledItem.Subtitle, filledItem.Icon, context,
                    request.InlineSuggestionsRequest, index);
                datasetBuilder.SetInlinePresentation(inlinePresentation);
            }

            if (filledItem.ApplyToFields(fields, datasetBuilder))
            {
                return datasetBuilder.Build();
            }
            return null;
        }

        public static Dataset BuildVaultDataset(Context context, FieldCollection fields, string uri, bool locked,
            FillRequest request, int index)
        {
            var intent = new Intent(context, typeof(MainActivity));
            intent.PutExtra("autofillFramework", true);
            if (fields.FillableForLogin)
            {
                intent.PutExtra("autofillFrameworkFillType", (int)CipherType.Login);
            }
            else if (fields.FillableForCard)
            {
                intent.PutExtra("autofillFrameworkFillType", (int)CipherType.Card);
            }
            else if (fields.FillableForIdentity)
            {
                intent.PutExtra("autofillFrameworkFillType", (int)CipherType.Identity);
            }
            else
            {
                return null;
            }
            intent.PutExtra("autofillFrameworkUri", uri);
            var pendingIntent = PendingIntent.GetActivity(context, ++_pendingIntentId, intent,
                PendingIntentFlags.CancelCurrent);

            var view = BuildListView(
                AppResources.AutofillWithBitwarden,
                locked ? AppResources.VaultIsLocked : AppResources.GoToMyVault,
                Resource.Drawable.icon,
                context);

            var datasetBuilder = new Dataset.Builder(view);
            datasetBuilder.SetAuthentication(pendingIntent.IntentSender);

            if (Build.VERSION.SdkInt > BuildVersionCodes.Q && request.InlineSuggestionsRequest != null)
            {
                var inlinePresentation = BuildInlinePresentation(
                    AppResources.AutofillWithBitwarden,
                    null,
                    Resource.Drawable.shield,
                    context,
                    request.InlineSuggestionsRequest, index);
                datasetBuilder.SetInlinePresentation(inlinePresentation);
            }

            // Dataset must have a value set. We will reset this in the main activity when the real item is chosen.
            foreach (var autofillId in fields.AutofillIds)
            {
                datasetBuilder.SetValue(autofillId, AutofillValue.ForText("PLACEHOLDER"));
            }
            return datasetBuilder.Build();
        }

        public static PendingIntent BuildAttributionIntent(Context context)
        {
            var intent = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
            intent.SetData(Uri.Parse($"package:{context.PackageName}"));
            return PendingIntent.GetActivity(context, 0, intent, 0);
        }

        public static InlinePresentation BuildInlinePresentation(string text, string subtext, int iconId,
            Context context, InlineSuggestionsRequest inlineRequest, int index)
        {
            var attribution = BuildAttributionIntent(context);
            var slice = BuildSlice(text, subtext, iconId, context, attribution);
            var spec = inlineRequest.InlinePresentationSpecs[index];
            return new InlinePresentation(slice, spec, false);
        }

        public static Slice BuildSlice(string text, string subtext, int iconId, Context context, PendingIntent pendingIntent)
        {
            var icon = Icon.CreateWithResource(context, iconId);
            var builder = InlineSuggestionUi.NewContentBuilder(pendingIntent);

            if (!TextUtils.IsEmpty(text))
                builder.SetTitle(text);

            if (!TextUtils.IsEmpty(subtext))
                builder.SetSubtitle(subtext);

            if (icon != null)
                builder.SetStartIcon(icon);

            return builder.Build().Slice;
        }

        public static RemoteViews BuildListView(string text, string subtext, int iconId, Context context)
        {
            var packageName = context.PackageName;
            var view = new RemoteViews(packageName, Resource.Layout.autofill_listitem);
            view.SetTextViewText(Resource.Id.text1, text);
            view.SetTextViewText(Resource.Id.text2, subtext);
            view.SetImageViewResource(Resource.Id.icon, iconId);
            return view;
        }

        public static void AddSaveInfo(Parser parser, FillResponse.Builder responseBuilder, FieldCollection fields)
        {
            // Docs state that password fields cannot be reliably saved in Compat mode since they will show as
            // masked values.
            var compatBrowser = CompatBrowsers.Contains(parser.PackageName);
            if (compatBrowser && fields.SaveType == SaveDataType.Password)
            {
                return;
            }

            var requiredIds = fields.GetRequiredSaveFields();
            if (fields.SaveType == SaveDataType.Generic || requiredIds.Length == 0)
            {
                return;
            }

            var saveBuilder = new SaveInfo.Builder(fields.SaveType, requiredIds);
            var optionalIds = fields.GetOptionalSaveIds();
            if (optionalIds.Length > 0)
            {
                saveBuilder.SetOptionalIds(optionalIds);
            }
            if (compatBrowser)
            {
                saveBuilder.SetFlags(SaveFlags.SaveOnAllViewsInvisible);
            }
            responseBuilder.SetSaveInfo(saveBuilder.Build());
        }
    }
}
