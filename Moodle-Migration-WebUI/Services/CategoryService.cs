using Microsoft.AspNetCore.SignalR;
using Microsoft.SqlServer.Server;
using Moodle_Migration.Interfaces;
using Moodle_Migration.Models;
using Moodle_Migration_WebUI.Hubs;
using Moodle_Migration_WebUI.Interfaces;
using Moodle_Migration_WebUI.Models;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography.X509Certificates;
using static System.Formats.Asn1.AsnWriter;

namespace Moodle_Migration.Services
{
    
    public class CategoryService : ICategoryService
    {
        private readonly IHttpService httpService;
        private readonly IComponentRepository componentRepository;
        private readonly IFileService fileService;
        private readonly IHubContext<StatusHub> hubContext;
        private readonly ILoggingRepository loggingRepository;
        private readonly IHttpContextAccessor _httpContextAccessor;
        public CategoryService(IHttpService _httpService, IComponentRepository _componentRepository, IFileService _fileService, IHubContext<StatusHub> _hubContext, ILoggingRepository _loggingRepository, IHttpContextAccessor httpContextAccessor)
        {

            httpService = _httpService;
            componentRepository = _componentRepository;
            fileService = _fileService;
            hubContext = _hubContext;
            loggingRepository = _loggingRepository;
            _httpContextAccessor = httpContextAccessor;
        }

        private readonly IHubContext<StatusHub> _hubContext;
        public async Task<string> ProcessCategory(string[] args)
        {
            string result = string.Empty;
            if (args.Length < 2)
            {
                result = "No category options specified!";
            }
            if (string.IsNullOrEmpty(result))
            {
                args = args.Skip(1).ToArray();
                var parameters = args.Skip(1).ToArray();

                switch (args![0])
                {
                    case "-d":
                    case "--display":
                        result = await GetCategories(parameters);
                        break;
                    case "-c":
                    case "--create":
                        result = await CreateCategoryStructure(parameters);
                        break;
                    default:
                        result = "Invalid category option!";
                        break;
                }
            }

            Console.Write(result);
            Console.WriteLine();
            return result;
        }

        private async Task<string> GetCategories(string[] parameters)
        {
            string additionalParameters = string.Empty;

            if (parameters.Length > 0)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!parameters[i].Contains("="))
                    {
                        return ($"Parameters must be in the format 'key=value' ({parameters[i]})");
                    }
                    var key = parameters[i].Split('=')[0];
                    var value = parameters[i].Split('=')[1];
                    additionalParameters += $"&criteria[{i}][key]={key}&criteria[{i}][value]={value}";
                }
            }

            string url = $"&wsfunction=core_course_get_categories{additionalParameters}";
            return await httpService.Get(url);
        }

        private async Task<string> CreateCategoryStructure(string[] parameters)
        {
            string result = string.Empty;

            if (parameters.Length == 0)
            {
                return "No category data specified!";
            }
            if (parameters.Length > 1)
            {               
                return "A single parameter in the format 'parameter=value' is required. (The 'parameter' can be 'id') \n";
            }
            if (!parameters[0].Contains("="))
            {
                return "Parameters must be in the format 'parameter=value')";
            }

            var parameter = parameters[0].Split('=')[0];
            var value = parameters[0].Split('=')[1];

            switch (parameter)
            {
                case "id":
                    {
                        int elfhComponentId = 0;
                        Int32.TryParse(value, out elfhComponentId);

                        if (elfhComponentId == 0)
                        {
                            return "Invalid elfh component ID!";
                        }
                        ElfhComponent elfhComponent = await componentRepository.GetByIdAsync(elfhComponentId);
                        if (elfhComponent == null)
                        {
                            return "Invalid elfh component ID!";
                        }
                        //check if category exists in moodle
                        var cats = await GetCategories(new[] { $"idnumber=elfh-{value}" });
                        var elfhChildComponents = await componentRepository.GetChildComponentsAsync(elfhComponent.ComponentId);
                        var catArray = string.IsNullOrWhiteSpace(cats) ? null : JArray.Parse(cats);
                        var category = catArray?.FirstOrDefault();
                        if (category == null)
                        {
                            result += $"Creating category '{elfhComponent.ComponentName}'";
                            var categoryResult = await CreateMoodleCategory(elfhComponent);
                            elfhComponent.MoodleCategoryId = categoryResult.resultValue;
                            result += $"Category '{elfhComponent.ComponentName}' - {categoryResult.result}";
                            result += await CreateCategoryChildren(elfhComponent, elfhChildComponents);
                            return result;
                        }
                        int catId = category["id"].Value<int>();
                        elfhComponent.MoodleCategoryId = catId;
                        var coursesJson = await GetCourses(new[] { $"category={catId}" });
                        var courseArray = JObject.Parse(coursesJson)["courses"] as JArray ?? new JArray();
                        var elfhCourses = elfhChildComponents
                                            .Where(c => c.ComponentTypeId == (int)ComponentTypeEnum.Course || c.ComponentTypeId == (int)ComponentTypeEnum.LearningPath)
                                            .ToList();
                        int moodleCourseCount = courseArray.Count;
                        int elfhCourseCount = elfhCourses.Count;
                        if (moodleCourseCount == elfhCourseCount)
                        {
                            result += $"Category '{elfhComponent.ComponentName}' already synced.";
                            return result;
                        }
                        result += $"Syncing missing courses for '{elfhComponent.ComponentName}'";
                        //  Extract existing Moodle course IDs (from idnumber)
                        var existingCourseIds = new HashSet<int>(
                            courseArray
                                .Where(c => c["idnumber"] != null)
                                .Select(c => c["idnumber"].ToString().Replace("elfh-", ""))
                                .Select(int.Parse)
                        );
                        var remainingComponents = elfhChildComponents
                                .Where(x =>
                                    !((x.ComponentTypeId == (int)ComponentTypeEnum.Course || x.ComponentTypeId == (int)ComponentTypeEnum.LearningPath) &&
                                       existingCourseIds.Contains(x.ComponentId))
                                )
                                .ToList();
                        remainingComponents = remainingComponents
                                .Where(x =>
                                    !(x.ComponentTypeId == (int)ComponentTypeEnum.Session &&
                                      existingCourseIds.Contains(x.ParentComponentId))
                                )
                                .ToList();
                        // Continue processing ONLY remaining items
                        result += await CreateCategoryChildren(elfhComponent, remainingComponents);
                        break;

                    }
                    

                case "idweb":
                    {
                        int elfhComponentId = 0;
                        Int32.TryParse(value, out elfhComponentId);

                        if (elfhComponentId == 0)
                        {
                            return "Invalid elfh component ID!";
                        }
                        ElfhComponent elfhComponent = await componentRepository.GetByIdAsync(elfhComponentId);
                        if (elfhComponent == null)
                        {
                            return "Invalid elfh component ID!";
                        }
                        var categoryResult = await CreateMoodleCategory(elfhComponent);
                        elfhComponent.MoodleCategoryId = categoryResult.resultValue;
                        result = categoryResult.result;

                        if (elfhComponent.MoodleCategoryId == 0)
                        {
                            result += "\n" + "Category creation failed!";
                            return result;
                        }

                        result += "\n" + $"The child elfh components will be created and added  to the '{elfhComponent.ComponentName}' category";

                        List<ElfhComponent> elfhChildComponentsWeb = await componentRepository.GetChildComponentsAsync(elfhComponent.ComponentId);
                        result += await CreateCategoryChildren(elfhComponent, elfhChildComponentsWeb);

                        break;
                    }
                default:
                    result = "Parameter must 'id'";
                    break;
            }
            return result;
        }

        private async Task<(string result, int resultValue)> CreateMoodleCategory(ElfhComponent? elfhComponent)
        {
            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            if (elfhComponent == null)
            {
                return ("Elfh component not found!", 0);
            }
            else
            {
                Dictionary<string, string> parameters = new Dictionary<string, string>
                    {
                    { "categories[0][name]", elfhComponent.ComponentName },
                    { "categories[0][parent]", elfhComponent.MoodleParentCategoryId.ToString() },
                    { "categories[0][idnumber]", $"elfh-{elfhComponent.ComponentId}" },
                    { "categories[0][description]", elfhComponent.ComponentDescription },
                    { "categories[0][descriptionformat]", "1" },
                    { "categories[0][theme]", "" }
                };

                Console.WriteLine($"Creating category '{elfhComponent.ComponentName}'");
                await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Creating category '"+ elfhComponent.ComponentName+"'");
                string url = "&wsfunction=core_course_create_categories";

                return await httpService.Post(url, parameters);
            }
        }

        private async Task<(string result, int resultValue)> CreateMoodleFolder(ElfhComponent? elfhComponent)
        {
            if (elfhComponent == null)
            {
                return ("Elfh component not found!", 0);
            }
            else
            {
                Dictionary<string, string> parameters = new Dictionary<string, string>
                    {
                   
                    { "parentcategoryid", elfhComponent.MoodleParentCategoryId.ToString() },
                     { "name", elfhComponent.ComponentName },
                    { "idnumber", "" },
                    { "description", "" }
                };

                Console.WriteLine($"Creating category '{elfhComponent.ComponentName}'");
                string url = "&wsfunction=local_custom_service_create_subfolder";

                var result = await httpService.Post(url, parameters);
                Console.WriteLine("Folder " + result.result);
                return result;
            }
        }


        private async Task<string> CreateCategoryChildren(ElfhComponent elfhComponent, List<ElfhComponent> elfhChildComponents)
        {
            string result = string.Empty;
            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            var parentId = elfhComponent.ComponentId;

            var folderIds = elfhChildComponents.Where(c => c.ParentComponentId == parentId && c.ComponentTypeId == (int)ComponentTypeEnum.Folder).Select(c => c.ComponentId).ToHashSet();

            List<ElfhComponent> children = elfhChildComponents.Where(c =>(c.ParentComponentId == parentId && c.ComponentTypeId != (int)ComponentTypeEnum.Folder)|| folderIds.Contains(c.ParentComponentId))
                .OrderBy(c => c.Position).ThenBy(c => c.ComponentId).ToList();

            if (children.Count > 0)
            {
                Console.WriteLine($"Processing {children.Count}  child objects for '{elfhComponent.ComponentName}'");
                await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Processing "+ children.Count+ " child objects for '"+ elfhComponent.ComponentName+"'");
            }
            foreach (var elfhChildComponent in children)
            {
                elfhChildComponent.MoodleParentCategoryId = elfhComponent.MoodleCategoryId;
                elfhChildComponent.MoodleCourseId = elfhComponent.MoodleCourseId;
                elfhChildComponent.DestinationCourseCategoriesId = elfhComponent.MoodleCategoryId == 0 ? elfhComponent.MoodleParentCategoryId : elfhComponent.MoodleCategoryId;
                switch ((ComponentTypeEnum)elfhChildComponent.ComponentTypeId)
                {
                    case ComponentTypeEnum.ClinicalGroup:

                        result += "\n" + $"Clinical group  '{elfhComponent.ComponentName}' ";
                        break;
                    case ComponentTypeEnum.Programme:
                    case ComponentTypeEnum.Folder:
                        if(elfhChildComponent.MoodleParentCategoryId>0)
                        {
                            result += "\n" + $"Creating {children.Count} child categories for '{elfhComponent.ComponentName}'";
                            var categoryResult = await CreateMoodleCategory(elfhChildComponent);
                            elfhChildComponent.MoodleCategoryId = categoryResult.resultValue;
                            result += categoryResult.result;
                            elfhChildComponent.DestinationCourseCategoriesId = categoryResult.resultValue;
                            await CreateCategoryChildren(elfhChildComponent, elfhChildComponents);
                        }
                        
                        break;
                    case ComponentTypeEnum.Application:
                        result += "\n" + $"Application '{elfhChildComponent.ComponentName}'";
                        break;
                    case ComponentTypeEnum.Course:
                        result += "\n" + $"Course '{elfhChildComponent.ComponentName}' - ";
                        var course = await CreateCourse(elfhChildComponent);
                        elfhChildComponent.MoodleCourseId = course.resultValue;
                        result += course.result;
                        await CreateCategoryChildren(elfhChildComponent, elfhChildComponents);
                        break;
                    case ComponentTypeEnum.LearningPath:
                        result += "\n" + $"Learning Path '{elfhChildComponent.ComponentName}' - ";
                        var learningpath = await CreateCourse(elfhChildComponent);
                        elfhChildComponent.MoodleCourseId = learningpath.resultValue;
                        result += learningpath.result;
                        await CreateCategoryChildren(elfhChildComponent, elfhChildComponents);
                        break;
                    case ComponentTypeEnum.Session:
                        result += "\n" + $"Creating Session '{elfhChildComponent.ComponentName}'";
                        var scorm = await CreateScorm(elfhChildComponent);
                        result += "\n" + $"Session '{elfhChildComponent.ComponentName}' has been created  ";
                        elfhChildComponent.DestinationScormId = scorm.scormId;
                        elfhChildComponent.DesitinationCourseSectionsId = scorm.sectionId;
                        elfhChildComponent.DestinationCourseId = elfhChildComponent.MoodleCourseId;
                        var loggingData = SetLoggingData(elfhChildComponent);
                        await loggingRepository.InsertLog(loggingData);
                        await CreateCategoryChildren(elfhChildComponent, elfhChildComponents);
                        break;
                    default:
                        break;
                }
            }
            return result;
        }
        private LoggingModel SetLoggingData(ElfhComponent elfhChildComponent)
        {
            LoggingModel loggingModel = new LoggingModel();
            loggingModel.SourceComponentId = elfhChildComponent.ComponentId;
            loggingModel.MigrationDateTime = DateTime.Now;
            loggingModel.SourceComponentHierarchyId = elfhChildComponent.SourceComponentHierarchyId;
            loggingModel.SourceParentComponentId = elfhChildComponent.SourceParentComponentId;
            loggingModel.SourceProgrammeComponentId = elfhChildComponent.SourceProgrammeComponentId;
            loggingModel.SourceCourseComponentId = elfhChildComponent.SourceParentComponentId;
            loggingModel.SourceDevelopmentId = elfhChildComponent.SourceDevelopmentId;
            loggingModel.SourceAmendDate = elfhChildComponent.SourceAmendDate;
            loggingModel.SourceAmendDate = elfhChildComponent.SourceAmendDate;
            loggingModel.DestinationCourseCategoriesId = elfhChildComponent.DestinationCourseCategoriesId;
            loggingModel.DesitinationCourseSectionsId = elfhChildComponent.DesitinationCourseSectionsId;
            loggingModel.DestinationCourseId = elfhChildComponent.DestinationCourseId;
            loggingModel.DestinationScormId = elfhChildComponent.DestinationScormId;
            loggingModel.CreateUser = 4;
            loggingModel.CreateDate = DateTimeOffset.Now;
            loggingModel.AmendDate = DateTimeOffset.Now;

            return loggingModel;
        }
        private async Task<(string result, int resultValue)> CreateCourse(ElfhComponent? elfhComponent)
        {
            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            if (elfhComponent == null)
            {
                return ("Elfh component not found!", 0);
            }
            else
            {
                Dictionary<string, string> parameters = new Dictionary<string, string>
                {
                    { "courses[0][fullname]", elfhComponent.ComponentName },
                    { "courses[0][shortname]", elfhComponent.ComponentName },
                    { "courses[0][categoryid]", elfhComponent.MoodleParentCategoryId.ToString() },
                    { "courses[0][idnumber]", $"elfh-{elfhComponent.ComponentId}" },
                    { "courses[0][summary]", elfhComponent.ComponentDescription },
                    { "courses[0][lang]", "en" }
                };

                //// Potential Course attributes
                //courses[0][fullname] = string
                //courses[0][shortname] = string
                //courses[0][categoryid] = int
                //courses[0][idnumber] = string
                //courses[0][summary] = string
                //courses[0][summaryformat] = int
                //courses[0][format] = string
                //courses[0][showgrades] = int
                //courses[0][newsitems] = int
                //courses[0][startdate] = int
                //courses[0][enddate] = int
                //courses[0][numsections] = int
                //courses[0][maxbytes] = int
                //courses[0][showreports] = int
                //courses[0][visible] = int
                //courses[0][hiddensections] = int
                //courses[0][groupmode] = int
                //courses[0][groupmodeforce] = int
                //courses[0][defaultgroupingid] = int
                //courses[0][enablecompletion] = int
                //courses[0][completionnotify] = int
                //courses[0][lang] = string
                //courses[0][forcetheme] = string
                //courses[0][courseformatoptions][0][name] = string
                //courses[0][courseformatoptions][0][value] = string
                //courses[0][customfields][0][shortname] = string
                //courses[0][customfields][0][value] = string
                await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Creating '"+ elfhComponent.ComponentName+  "' in moodle. Please wait.");
                string url = "&wsfunction=core_course_create_courses";
                var result = await httpService.Post(url, parameters);
                return result;
            }
        }
        private async Task<(string result, int scormId, int sectionId)> CreateScorm(ElfhComponent elfhComponent)
        {

            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            if (elfhComponent == null)
            {
                return ("Elfh component not found!", 0, 0);
            }
            else
            {
                var zipBytes = await fileService.DownloadFileAsync(elfhComponent.SourceDevelopmentId);
                if (zipBytes == null)
                {
                    await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Scorm file not found in content server.");
                    return ("Scorm file not found in content server.", 0, 0);
                }
                // Convert to Base64
                string base64Zip = Convert.ToBase64String(zipBytes);

                Dictionary<string, string> parameters = new Dictionary<string, string>
                {
                    { "courseid", elfhComponent.MoodleCourseId.ToString() },
                    { "section", "0" },
                    { "scormname", elfhComponent.ComponentName },
                    { "foldername", elfhComponent.SourceDevelopmentId },
                    { "base64Zip", base64Zip }
                };



                string url = "&wsfunction=mod_scorm_insert_scorm_resource";
                await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Creating scorm resource '" + elfhComponent.ComponentName + "'  in moodle. Please wait.");
                Console.WriteLine("Creating scorm resource in moodle");
                var result = await httpService.PostScorm(url, parameters);
                await hubContext.Clients.User(currentUser).SendAsync("ReceiveStatus", "Scorm " + elfhComponent.ComponentName + "'- " + result.result);
                Console.WriteLine("Scorm resource '" + elfhComponent.ComponentName + "'- " + result.result);
                return result;
            }
        }
        private async Task<string> GetCourses(string[] parameters)
        {
            string additionalParameters = string.Empty;

            if (parameters.Length == 1) // field and value are provided
            {
                if (!parameters[0].Contains("="))
                {
                    return ($"Parameters must be in the format 'field=value' ({parameters[0]})");
                }
                var key = parameters[0].Split('=')[0];
                var value = parameters[0].Split('=')[1];
                additionalParameters = $"&field={key}&value={value}";
            }

            string url = $"&wsfunction=core_course_get_courses_by_field{additionalParameters}";
            return await httpService.Get(url);
        }
    }
}