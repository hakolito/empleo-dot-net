﻿using System.Threading.Tasks;
using System.Web.Mvc;
using EmpleoDotNet.Core.Dto;
using EmpleoDotNet.Helpers;
using EmpleoDotNet.AppServices;
using EmpleoDotNet.Helpers.Alerts;
using EmpleoDotNet.Services.Social.Twitter;
using EmpleoDotNet.ViewModel;
using EmpleoDotNet.ViewModel.JobOpportunity;
using reCAPTCHA.MVC;
using System;
using System.Linq;
using System.Net;
using EmpleoDotNet.Core.Domain;
using EmpleoDotNet.ViewModel.JobOpportunityLike;
using Microsoft.AspNet.Identity;
using Tweetinvi.Logic.JsonConverters;

namespace EmpleoDotNet.Controllers
{
    public class JobOpportunityController : EmpleoDotNetController
    {
        public ActionResult Index(JobOpportunityPagingParameter model)
        {
            var viewModel = GetSearchViewModel(model);

            if (!string.IsNullOrWhiteSpace(viewModel.SelectedLocationName) &&
                string.IsNullOrWhiteSpace(viewModel.SelectedLocationPlaceId))
            {
                ModelState.AddModelError("SelectedLocationName", "");
                return View(viewModel).WithError("Debe seleccionar una Localidad para buscar.");
            }

            var jobOpportunities = _jobOpportunityService.GetAllJobOpportunitiesPagedByFilters(model);

            viewModel.Result = jobOpportunities;

            return View(viewModel);
        }

        // GET: /jobs/4-jobtitle
        public ActionResult Detail(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction(nameof(Index));

            var jobOpportunityId = GetIdFromTitle(id);

            if (jobOpportunityId == 0)
                return RedirectToAction(nameof(Index));

            var jobOpportunity = _jobOpportunityService.GetJobOpportunityById(jobOpportunityId);

            if (jobOpportunity == null)
                return View(nameof(Index))
                    .WithError("La vacante solicitada no existe. Por favor escoge una vacante válida del listado");

            var expectedUrl = UrlHelperExtensions.SeoUrl(jobOpportunityId, jobOpportunity.Title.SanitizeUrl());

            if (!expectedUrl.Equals(id, StringComparison.OrdinalIgnoreCase))
                return RedirectToActionPermanent(nameof(Detail), new { id = expectedUrl });

            ViewBag.RelatedJobs =
                _jobOpportunityService.GetCompanyRelatedJobs(jobOpportunityId, jobOpportunity.CompanyName);

            ViewBag.CanLike = !CookieHelper.Exists(GetLikeCookieName(jobOpportunityId));

            var cookieView = $"JobView{jobOpportunity.Id}";

            if (IsJobOpportunityOwner(id) || CookieHelper.Exists(cookieView))
            {
                return jobOpportunity.IsHidden
                    ? View(nameof(Detail), jobOpportunity).WithInfo(Constants.JobDetailWithInfoMessage)
                    : View(nameof(Detail), jobOpportunity);
            }

            _jobOpportunityService.UpdateViewCount(jobOpportunity.Id);
            CookieHelper.Set(cookieView, jobOpportunity.Id.ToString());

            return jobOpportunity.IsHidden
                ? View(nameof(Detail), jobOpportunity).WithInfo(Constants.JobDetailWithInfoMessage)
                : View(nameof(Detail), jobOpportunity);
        }

        [HttpGet]

        [Authorize]
        public ActionResult New()
        {
            var viewModel = new NewJobOpportunityViewModel();

            return View(viewModel)
                .WithInfo("Prueba nuestro nuevo proceso guiado de creación de posiciones haciendo <b><a href='" + Url.Action("Wizard") + "'>click aquí</a></b>");
        }

        [HttpPost, ValidateAntiForgeryToken]
        [ValidateInput(false)]
        [CaptchaValidator(RequiredMessage = "Por favor confirma que no eres un robot")]
        [Authorize]
        public async Task<ActionResult> New(NewJobOpportunityViewModel model, bool captchaValid)
        {
            if (!ModelState.IsValid)
            {
                return View(model)
                    .WithError("Han ocurrido errores de validación que no permiten continuar el proceso");
            }

            if (string.IsNullOrWhiteSpace(model.LocationPlaceId))
            {
                ModelState.AddModelError(nameof(model.LocationName), "");
                return View(model).WithError("Debe seleccionar una Localidad.");
            }

            if (!string.IsNullOrWhiteSpace(model.CompanyLogoUrl) && !UrlHelperExtensions.IsImageAvailable(model.CompanyLogoUrl))
            {
                return View(model).WithError("La url del logo debe ser a una imagen en formato png o jpg");
            }

            var jobOpportunity = model.ToEntity();
            var userId = User.Identity.GetUserId();

            _jobOpportunityService.CreateNewJobOpportunity(jobOpportunity, userId);

            await _twitterService.PostNewJobOpportunity(jobOpportunity, Url).ConfigureAwait(false);

            return RedirectToAction(nameof(Detail), new
            {
                id = UrlHelperExtensions.SeoUrl(jobOpportunity.Id, jobOpportunity.Title)
            });
        }

        [HttpGet]
        public ActionResult Wizard()
        {
            var viewModel = new Wizard();

            return View(viewModel);
        }

        [HttpGet]
        [Authorize]
        public ActionResult Edit(string title)
        {
            var job = GetJobOpportunityFromTitle(title);

            if (!IsJobOpportunityOwner(title))
                return RedirectToAction("Detail", new { id = title });

            var wizardvm = ViewModel.JobOpportunity.Wizard.FromEntity(job);
            return View("Wizard", wizardvm);
        }

        [Authorize]
        public ActionResult Delete(string title, bool returnPrevious = true)
        {
            var jobOpportunity = GetJobOpportunityFromTitle(title);
            if (IsJobOpportunityOwner(title))
            {
                _jobOpportunityService.SoftDeleteJobOpportunity(jobOpportunity);
            }

            if (Request.UrlReferrer == null || returnPrevious == false)
            {
                return RedirectToAction("Index", "Home")
                        .WithSuccess($"Se ha borrado exitosamente la oportunidad de empleo: {jobOpportunity.Title}");
            }

            return Redirect(Request.UrlReferrer.ToString())
                        .WithSuccess($"Se ha borrado exitosamente la oportunidad de empleo: {jobOpportunity.Title}");
        }

        [HttpPost]
        public JsonResult ToggleHide(string title)
        {
            var jobOpportunity = GetJobOpportunityFromTitle(title);
            if (IsJobOpportunityOwner(title))
            {
                _jobOpportunityService.ToggleHideState(jobOpportunity);
            }

            return Json(new { isHidden = jobOpportunity.IsHidden });

        }

        [HttpPost, ValidateAntiForgeryToken]
        [ValidateInput(false)]
        [CaptchaValidator(RequiredMessage = "Por favor confirma que no eres un robot", ErrorMessage = "El captcha es incorrecto.")]
        public async Task<ActionResult> Wizard(Wizard model)
        {
            if (!ModelState.IsValid)
                return View(model)
                    .WithError("Han ocurrido errores de validación que no permiten continuar el proceso");

            var jobOpportunity = model.ToEntity();
            var jobExists = _jobOpportunityService.JobExists(model.Id);

            if (!jobExists)
            {
                _jobOpportunityService.CreateNewJobOpportunity(jobOpportunity, User.Identity.GetUserId());
            }
            else
            {
                _jobOpportunityService.UpdateJobOpportunity(model.Id, model.ToEntity());
            }

            await _twitterService.PostNewJobOpportunity(jobOpportunity, Url);

            return RedirectToAction(nameof(Detail), new
            {
                id = UrlHelperExtensions.SeoUrl(jobOpportunity.Id, jobOpportunity.Title),
                fromWizard = 1
            });
        }

        [HttpPost]
        public JsonResult Like(JobOpportunityLike model)
        {
            var cookieName = GetLikeCookieName(model.JobOpportunityId);

            if (CookieHelper.Exists(cookieName))
            {
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return Json(new { error = true, message = "Ya has votado por este empleo." });
            }

            _jobOpportunityLikeService.CreateNewLike(model);

            CookieHelper.Set(cookieName, model.JobOpportunityId.ToString());

            var jobLikeData = _jobOpportunityLikeService.GetLikesByJobOpportunityId(model.JobOpportunityId);

            var jobOpportunityLikeData = new JobOpportunityLikeViewModel
            {
                Likes = jobLikeData.Count(x => x.Like),
                DisLikes = jobLikeData.Count(x => !x.Like)
            };

            return Json(new { error = false, data = jobOpportunityLikeData });
        }

        /// <summary>
        /// Transform JobOpportunityPagingParameter into JobOpportunitySearchViewModel with Locations
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        private JobOpportunitySearchViewModel GetSearchViewModel(JobOpportunityPagingParameter model)
        {
            if (string.IsNullOrWhiteSpace(model.SelectedLocationName))
            {
                model.SelectedLocationLatitude = string.Empty;
                model.SelectedLocationLongitude = string.Empty;
                model.SelectedLocationPlaceId = string.Empty;
            }

            var viewModel = new JobOpportunitySearchViewModel
            {
                SelectedLocationPlaceId = model.SelectedLocationPlaceId,
                SelectedLocationName = model.SelectedLocationName,
                SelectedLocationLongitude = model.SelectedLocationLongitude,
                SelectedLocationLatitude = model.SelectedLocationLatitude,
                JobCategory = model.JobCategory,
                Keyword = model.Keyword,
                IsRemote = model.IsRemote,
                CategoriesCount = _jobOpportunityService.GetMainJobCategoriesCount(),
            };

            return viewModel;
        }

        private static string GetLikeCookieName(int jobOpportunityId)
        {
            return $"JobLike{jobOpportunityId}";
        }

        private static int GetIdFromTitle(string title)
        {
            int id;
            var url = title.Split('-');

            if (string.IsNullOrEmpty(title) || url.Length == 0 || !int.TryParse(url[0], out id))
                return 0;

            return id;
        }

        private JobOpportunity GetJobOpportunityFromTitle(string title)
        {
            var jobId = GetIdFromTitle(title);
            return _jobOpportunityService.GetJobOpportunityById(jobId);
        }

        private bool IsJobOpportunityOwner(string title)
        {
            var jobOpportunity = GetJobOpportunityFromTitle(title);
            var currentUser = User.Identity.GetUserId();
            return (currentUser != null && jobOpportunity.UserProfile?.UserId == currentUser);
        }

        public JobOpportunityController(
            IJobOpportunityService jobOpportunityService,
            ITwitterService twitterService,
            IJobOpportunityLikeService jobOpportunityLikeService)
        {
            _jobOpportunityService = jobOpportunityService;
            _twitterService = twitterService;
            _jobOpportunityLikeService = jobOpportunityLikeService;
        }

        private readonly IJobOpportunityService _jobOpportunityService;
        private readonly ITwitterService _twitterService;
        private readonly IJobOpportunityLikeService _jobOpportunityLikeService;
    }
}